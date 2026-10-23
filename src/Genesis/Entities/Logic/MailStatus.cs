// Moved from blocks-logic server/Mail.DomainService/Shared/Enums/MailStatus.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Logic;

public enum MailStatus
{
    Sent,
    Delivered,
    Bounced,
    Complained,
    Rejected,
    Received,
    Unknown
}
