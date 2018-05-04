using Microsoft.WindowsAzure.Storage.Table;

namespace AzureManagement.Models
{
    public class RequiredTag : TableEntity
    {
        public string Name { get; set; }
    }
}