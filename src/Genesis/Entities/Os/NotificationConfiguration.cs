// Moved from blocks-os server/Configuration.DomainService/Notification/Entities/NotificationConfiguration.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
using Blocks.Genesis;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Os;

[BsonIgnoreExtraElements]
public class NotificationConfiguration : BaseEntity
{
    public string Name { get; set; }
    public NotifierTypes ChannelToNotify { get; set; }
    public NotificationReceiverTypes NotificationType { get; set; }
    public string NotifyMethod { get; set; }
    public bool EnablePersistence { get; set; }
}
