using Claudalisk.Domain;

namespace Claudalisk.Jobs.Tests.Modules.Billing;

public sealed class RefundDenied() : DomainException("refund-denied", "The refund is denied.", DomainErrorKind.Conflict);
