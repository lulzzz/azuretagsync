using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using System;
using System.Linq;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using System.Collections.Generic;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using System.Threading.Tasks;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.WindowsAzure.Storage.Table;
using AzureManagement.Models;
using AzureManagement.Services;
using Microsoft.Azure.Services.AppAuthentication;

namespace AzureManagement.Function
{
    public static class AuditSubscriptionTags
    {
        static Dictionary<string, string> _apiVersions = new Dictionary<string, string>();
        static ICollector<string> _outQueue;
        static TraceWriter _log;
        static ResourceManagementClient _client;

        [FunctionName("AuditResourceGroups")]
        public static async Task Run(
            [TimerTrigger("0 0 */2 * * *")] TimerInfo myTimer,
            [Table("AuditConfig")] CloudTable configTbl,
            [Table("AuditStats")] CloudTable statsTbl,
            [Table("InvalidTagResources")] CloudTable invalidTypesTbl,
            [Queue("resources-to-tag")] ICollector<string> outQueue,
            TraceWriter log )
        {
            _log = log;
            _outQueue = outQueue;
            log.Info("Starding subscription audit.");
            var invalidTagResourcesQuery = await invalidTypesTbl.ExecuteQuerySegmentedAsync(new TableQuery<InvalidTagResource>(), null);
            var auditConfigQuery = await configTbl.ExecuteQuerySegmentedAsync(new TableQuery<AuditConfig>(), null);
            TokenCredentials tokenCredential;

            if (Environment.GetEnvironmentVariable("MSI_ENDPOINT") == null)
            {
                log.Info("Using service principal");
                string appId = Environment.GetEnvironmentVariable("appId");
                string appSecret = Environment.GetEnvironmentVariable("appSecret");
                string tenantId = Environment.GetEnvironmentVariable("tenantId");
                tokenCredential = AuthenticationService.GetAccessToken(appId, appSecret, tenantId);
                // _client = new ResourceManagementClient(AuthenticationService.GetAccessToken(appId, appSecret, tenantId));
            }
            else
            {
                log.Info("Using MSI");
                var azureServiceTokenProvider = new AzureServiceTokenProvider();
                string token = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.core.windows.net/");
                tokenCredential = new TokenCredentials(token);
                // _client = new ResourceManagementClient(new TokenCredentials(token));
            }

            try
            {
                _client = new ResourceManagementClient(tokenCredential);
            }
            catch(Exception ex)
            {
                log.Error("Unable to connect to the ARM API, Message: " + ex.Message);
            }

            // Init config table if new deployment
            if (auditConfigQuery.Results.Count == 0)
            {
                AuditConfig initConfig = new AuditConfig { SubscriptionId = "enter_valid_subscription_id", RowKey = Guid.NewGuid().ToString(), RequiredTags = "comma,separated,tag,list,here", PartitionKey = "init" };
                TableOperation insertOperation = TableOperation.InsertOrReplace(initConfig);
                await configTbl.ExecuteAsync(insertOperation);
                log.Info("First run for new deployment. Please populate the AuditConfig table.");
                return;
            }

            foreach (var auditConfig in auditConfigQuery.Results)
            {
                try
                {
                    AuditStats stats = new AuditStats { JobStart= DateTime.Now, PartitionKey = auditConfig.SubscriptionId, RowKey = Guid.NewGuid().ToString() };
                    IEnumerable<string> requiredTagsList = auditConfig.RequiredTags.Split(',');
                    _client.SubscriptionId = auditConfig.SubscriptionId;
                    IEnumerable<ResourceGroupInner> resourceGroups = await _client.ResourceGroups.ListAsync();
                    stats.ResourceGroupsTotal = resourceGroups.Count();
                    await ProcessResourceGroups(requiredTagsList, resourceGroups, invalidTagResourcesQuery.Results, auditConfig.SubscriptionId, stats);
                    log.Info("Completed audit of subscription: " + auditConfig.SubscriptionId);
                    stats.JobEnd = DateTime.Now;

                    TableOperation insertOperation = TableOperation.InsertOrReplace(stats);
                    await statsTbl.ExecuteAsync(insertOperation);
                }
                catch (Exception ex)
                {
                    log.Error("Failure processing resource groups for auditConfig: " + auditConfig.RowKey);
                    log.Error(ex.Message);
                }

            }
        }

        static async Task ProcessResourceGroups(IEnumerable<string> requiredTagsList, IEnumerable<ResourceGroupInner> resourceGroups, List<InvalidTagResource> invalidTypes, string subscriptionId, AuditStats stats )
        {
            foreach(ResourceGroupInner rg in resourceGroups)
            {
                _log.Info("*** Resource Group: " + rg.Name);

                Dictionary<string, string> requiredRgTags = new Dictionary<string, string>();
                foreach (string tagKey in requiredTagsList)
                {
                    if (rg.Tags != null && rg.Tags.ContainsKey(tagKey))
                    {
                        requiredRgTags.Add(tagKey, rg.Tags[tagKey]);
                    }
                }

                if (requiredRgTags.Count != requiredTagsList.Count())
                { 
                    _log.Warning("Resource group: " + rg.Name + " does not have required tags.");
                    stats.ResourceGroupsSkipped += 1;
                }
                else
                {
                    IEnumerable<GenericResourceInner> resources = await _client.Resources.ListByResourceGroupAsync(rg.Name);
                    stats.ResourceItemsTotal = resources.Count();

                    foreach(var resource in resources)
                    {
                        // InvalidTagResource invalidResourceMatch = invalidTagResourcesQuery.Results.Where(x => x.Type == resource.Type).FirstOrDefault();
                        InvalidTagResource invalidType = invalidTypes.Where(x => x.Type == resource.Type).FirstOrDefault();

                        if (invalidType == null)
                        {
                            string apiVersion;
                            
                            try
                            {
                                apiVersion = await GetApiVersion(_client, resource.Type);
                            }
                            catch(Exception ex)
                            {
                                _log.Error(ex.Message);
                                break;
                            }

                            var result = SetTags(resource.Tags, requiredRgTags);

                            if (result.Count > 0)
                            {
                                stats.ResourceItemsWithUpdates += 1;
                                resource.Tags = result;
                                ResourceItem newItem = new ResourceItem {   Id = resource.Id, 
                                            ApiVersion = apiVersion,
                                            Location = resource.Location,
                                            Tags = resource.Tags,
                                            Type = resource.Type,
                                            Subscription = subscriptionId
                                        };
                            
                                string messageText = JsonConvert.SerializeObject(newItem);
                                _outQueue.Add(messageText);
                                _log.Info("Requesting tags for: " + resource.Id);
                            }
                            else
                            {
                                stats.ResourceItemsSkipped += 1;
                            }
                        }
                        else
                        {
                            _log.Warning("Item type does not support tagging: " + resource.Type);
                            stats.ResourceItemsSkipped += 1;
                        }
                    }
                }
            }
            
        }


        static IDictionary<string, string> SetTags(IDictionary <string, string> resourceTags, Dictionary <string, string> requiredTags)
        {
            bool tagUpadateRequired = false;

            foreach(var requiredTag in requiredTags)
            {
                if (resourceTags == null) // resource does not have any tags. Set to the RG required tags and exit.
                {
                    return requiredTags;
                }
                if ( resourceTags.ContainsKey(requiredTag.Key) ) // resource has a matching rquired RG tag.
                {
                    if (resourceTags[requiredTag.Key] != requiredTag.Value) // update resource tag value if it doesn't match the current RG tag
                    {
                        resourceTags[requiredTag.Key] = requiredTag.Value;
                        tagUpadateRequired = true;
                    }
                }
                else
                {
                    resourceTags.Add(requiredTag);
                    tagUpadateRequired = true;
                }
            }

            if (tagUpadateRequired)
            {
                return resourceTags;
            }
            else
            {
                return new Dictionary<string, string>();
            }

        }

        static async Task<string> GetApiVersion(ResourceManagementClient client, string resource)
        {
            string resourceProvider = resource.Split('/')[0];
            string resourceType = resource.TrimStart(resourceProvider.ToCharArray()).TrimStart('/');

            if (_apiVersions.ContainsKey(resourceType))
            {
                return _apiVersions[resourceType];
            }
            else
            {
                try
                {
                    ProviderInner provider = await client.Providers.GetAsync(resourceProvider);
                    var resourceApi = provider.ResourceTypes.Where(x => x.ResourceType == resourceType).FirstOrDefault().ApiVersions[0];
                    _apiVersions.Add(resourceType, resourceApi);
                    return resourceApi;
                }
                catch(Exception ex) { throw(ex); }
            }
        }

        /*
        static TokenCredentials GetAccessToken()
        {
            string appId = Environment.GetEnvironmentVariable("appId");
            string appSecret = Environment.GetEnvironmentVariable("appSecret");
            string tenantId = Environment.GetEnvironmentVariable("tenantId");
            var authContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", tenantId));
            var credential = new ClientCredential(appId, appSecret);
            AuthenticationResult token = authContext.AcquireTokenAsync("https://management.core.windows.net/", credential).Result;
            return new TokenCredentials(token.AccessToken);
        }
        */

    }
}
