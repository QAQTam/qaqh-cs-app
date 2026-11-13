using Qaqh.Sdk.Contract;

namespace Qaqh.Sdk.Transport;

/// <summary>一次 open 协商出的租约状态（header 身份 + capability）。</summary>
public sealed record ClientLeaseState(
    string ClientInstanceId,
    string ClientSessionId,
    string ServerEpoch,
    long LeaseTtlMs,
    long RenewIntervalMs,
    RingingV2Capabilities Capabilities);
