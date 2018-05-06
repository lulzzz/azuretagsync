using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using AzureManagement.Models;
using Newtonsoft.Json;
using Microsoft.Rest;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.WindowsAzure.Storage.Table;
using System.Threading.Tasks;

namespace AzureManagement.Function
{
    public static class AddTags
    {
        [FunctionName("AddTags")]
        public static async void Run([QueueTrigger("resources-to-tag", Connection = "AzureWebJobsStorage")]string myQueueItem,
            [Table("InvalidTagResources")] CloudTable invalidResourceTable,
            TraceWriter log)
        {
            log.Info($"C# Queue trigger function triggered: {myQueueItem}");

            ResourceItem updateItem = JsonConvert.DeserializeObject<ResourceItem>(myQueueItem);

            var client = new ResourceManagementClient(GetAccessToken());
            client.SubscriptionId = updateItem.Subscription;

            GenericResourceInner resource = await client.Resources.GetByIdAsync(updateItem.Id, updateItem.ApiVersion);
            resource.Tags = updateItem.Tags;

            try
            {
                await client.Resources.UpdateByIdAsync(updateItem.Id, updateItem.ApiVersion, resource);
            }
            catch(Exception ex)
            {
                log.Error(resource.Id + " failed with: " + ex.Message);
                InvalidTagResource invalidItem = new InvalidTagResource { 
                    Type = updateItem.Type, 
                    Message = ex.Message,
                    RowKey = Guid.NewGuid().ToString(),
                    PartitionKey = updateItem.Subscription };

                TableOperation insertOperation = TableOperation.InsertOrReplace(invalidItem);
                await invalidResourceTable.ExecuteAsync(insertOperation);
                // await WriteInvalidTagResource(invalidResourceTable, insertOperation);
            }

        }

        static async Task<string> WriteInvalidTagResource(CloudTable table, TableOperation op)
        {
            try
            {
                await table.ExecuteAsync(op);
                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
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
