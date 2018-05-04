using Microsoft.WindowsAzure.Storage.Table;

namespace AzureManagement.Models
{
    public class InvalidTagResource : TableEntity
    {
        public string Id { get; set; }
        public string Message { get; set; }
    }
}