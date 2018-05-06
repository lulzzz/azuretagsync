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

namespace AzureManagement.Function
{
    public static class AuditResourceGroups
    {
        static Dictionary<string, string> apiVersions = new Dictionary<string, string>();

        [FunctionName("AuditResourceGroups")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req,
            [Table("RequiredTags")] CloudTable requiredTagsTbl,
            [Table("InvalidTagResources")] CloudTable invalidTypesTbl,
            [Queue("resources-to-tag")] ICollector<string> outQueue,
            TraceWriter log )
        {
            log.Info("C# HTTP trigger function processed a request.");

            // string appId = Environment.GetEnvironmentVariable("appId");
            // string appSecret = Environment.GetEnvironmentVariable("appSecret");
            // string tenantId = Environment.GetEnvironmentVariable("tenantId");
            // string subscriptionId = "cecea5c9-0754-4a7f-b5a9-46ae68dcafd3";

            // var creds = new AzureCredentialsFactory().FromServicePrincipal(appId, appSecret, tenantId, AzureEnvironment.AzureGlobalCloud);
            // var azure = Azure.Authenticate(creds).WithSubscription(subscriptionId);
            // IEnumerable<IResourceGroup> resourceGroups = azure.ResourceGroups.List();
            // IEnumerable<ResourceGroupInner> resourceGroups = await client.ResourceGroups.ListAsync();

            string subscriptionId = "cecea5c9-0754-4a7f-b5a9-46ae68dcafd3";
            var client = new ResourceManagementClient(GetAccessToken());
            client.SubscriptionId = subscriptionId;

            // Get data: subscription resources, resource groups, types that don't support tags, and required tags
            // IEnumerable<GenericResourceInner> resources = await client.Resources.ListAsync();
            IEnumerable<ResourceGroupInner> resourceGroups = await client.ResourceGroups.ListAsync();          
            var invalidTagResourcesQuery = await invalidTypesTbl.ExecuteQuerySegmentedAsync(new TableQuery<InvalidTagResource>(), null);
            var requiredTagsQuery = await requiredTagsTbl.ExecuteQuerySegmentedAsync(new TableQuery<RequiredTag>(), null);

            foreach(ResourceGroupInner rg in resourceGroups)
            {
                log.Info(rg.Name);

                Dictionary<string, string> requiredRgTags = new Dictionary<string, string>();
                foreach (RequiredTag tag in requiredTagsQuery.Results)
                {
                    if (rg.Tags != null && rg.Tags.ContainsKey(tag.Name))
                    {
                        requiredRgTags.Add(tag.Name, rg.Tags[tag.Name]);
                    }
                }

                if (requiredRgTags.Count < 1)
                { 
                    log.Warning("Resource group: " + rg.Name + " does not have required tags.");
                }
                else
                {
                    IEnumerable<GenericResourceInner> resources = await client.Resources.ListByResourceGroupAsync(rg.Name);

                    foreach(var resource in resources)
                    {
                        InvalidTagResource invalidResourceMatch = invalidTagResourcesQuery.Results.Where(x => x.Type == resource.Type).FirstOrDefault();

                        if (invalidResourceMatch == null)
                        {
                            string apiVersion;
                            
                            try
                            {
                                apiVersion = await GetApiVersion(client, resource.Type);
                            }
                            catch(Exception ex)
                            {
                                log.Error(ex.Message);
                                break;
                            }
                            
                            foreach(var tag in requiredRgTags)
                            {
                                if (resource.Tags == null)
                                {
                                    resource.Tags = requiredRgTags;
                                }
                                else if ( resource.Tags.ContainsKey(tag.Key) )
                                {
                                    resource.Tags[tag.Key] = tag.Value;
                                }
                                else { resource.Tags.Add(tag); }
                            }

                            ResourceItem newItem = new ResourceItem {   Id = resource.Id, 
                                                                        ApiVersion = apiVersion,
                                                                        Location = resource.Location,
                                                                        Tags = resource.Tags,
                                                                        Type = resource.Type,
                                                                        Subscription = subscriptionId
                                                                    };
                            
                            string messageText = JsonConvert.SerializeObject(newItem);
                            outQueue.Add(messageText);
                        }
                        else
                        {
                            log.Warning("Item type does not support tagging: " + resource.Type);
                        }
                    }

                }
            }

            return (ActionResult)new OkObjectResult("OK");
        }

        static async Task<string> GetApiVersion(ResourceManagementClient client, string resource)
        {
            string resourceProvider = resource.Split('/')[0];
            string resourceType = resource.TrimStart(resourceProvider.ToCharArray()).TrimStart('/');

            if (apiVersions.ContainsKey(resourceType))
            {
                return apiVersions[resourceType];
            }
            else
            {
                try
                {
                    ProviderInner provider = await client.Providers.GetAsync(resourceProvider);
                    var resourceApi = provider.ResourceTypes.Where(x => x.ResourceType == resourceType).FirstOrDefault().ApiVersions[0];
                    apiVersions.Add(resourceType, resourceApi);
                    return resourceApi;
                }
                catch(Exception ex) { throw(ex); }
            }
        }
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

    }
}
