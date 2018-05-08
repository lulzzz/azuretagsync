using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage.Table;

namespace AzureManagement.Models
{
    public class AuditConfig : TableEntity
    {
        public string SubscriptionId { get; set; }
        public string RequiredTags { get; set; }
    }   
}