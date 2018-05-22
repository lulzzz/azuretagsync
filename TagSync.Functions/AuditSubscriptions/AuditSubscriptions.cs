using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using TagSync.Models;
using TagSync.Services;

namespace TagSync.Functions
{
    public static class AuditSubscriptions
    {

        static ICollector<string> _outQueue;
        static TraceWriter _log;
        static ResourceManagerService _resourceManager;

        [FunctionName("AuditResourceGroups")]
        public static async Task Run(TimerInfo myTimer, CloudTable configTbl, CloudTable statsTbl, CloudTable invalidTypesTbl, ICollector<string> outQueue, TraceWriter log )
        {
            _log = log;
            _outQueue = outQueue;
            log.Info("Starding subscription audit.");
            var invalidTagResourcesQuery = await invalidTypesTbl.ExecuteQuerySegmentedAsync(new TableQuery<InvalidTagResource>(), null);
            var auditConfigQuery = await configTbl.ExecuteQuerySegmentedAsync(new TableQuery<AuditConfig>(), null);

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

                    try
                    {
                        string token = await AuthenticationService.GetAccessTokenAsync();
                        _resourceManager = new ResourceManagerService(token);
                    }
                    catch (Exception ex)
                    {
                        log.Error("Unable to connect to the ARM API, Message: " + ex.Message);
                    }

                    await ProcessResourceGroups(requiredTagsList, invalidTagResourcesQuery.Results, auditConfig.SubscriptionId, stats);
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

        static async Task ProcessResourceGroups(IEnumerable<string> requiredTagsList, List<InvalidTagResource> invalidTypes, string subscriptionId, AuditStats stats )
        {
            var resourceGroups = await _resourceManager.GetResourceGroups(subscriptionId);
            stats.ResourceGroupsTotal = resourceGroups.Count;

            foreach (var rg in resourceGroups)
            {
                _log.Info("*** Resource Group: " + rg.Name);
                var tagsToSync = TagService.GetRequiredTags((Dictionary<string, string>)rg.Tags, requiredTagsList);

                if (tagsToSync.Count < 1)
                { 
                    _log.Warning("Resource group: " + rg.Name + " does not have required tags.");
                    stats.ResourceGroupsSkipped += 1;
                }
                else
                {
                    List<ResourceItem> resources = await _resourceManager.GetResources(rg.Name, subscriptionId, invalidTypes.Select(t => t.Type).ToList());
                    stats.ResourceItemsTotal = resources.Count();

                    foreach(var resource in resources)
                    {
                        var result = TagService.GetTagUpdates(resource.Tags.ToDictionary(x => x.Key, x => x.Value), tagsToSync);

                        if (result.Count > 0)
                        {
                            stats.ResourceItemsWithUpdates += 1;
                            resource.Tags = result;
                            string messageText = JsonConvert.SerializeObject(resource);
                            _log.Info("Requesting tags for: " + resource.Id);
                            _outQueue.Add(messageText);
                        }
                        else
                        {
                            stats.ResourceItemsSkipped += 1;
                        }
                    }
                }
            }
        }

    }
}
