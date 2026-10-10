namespace Wanxiangshu.Mission.Relay

module Surface =
    val empty: unit -> RelayState

    val openRoad:
        road: string -> authority: string -> message: string -> incumbent: string -> snapshot: string -> RelayState

    val openIncumbency:
        state: RelayState -> road: string -> incumbent: string -> snapshot: string -> authority: string -> obj

    val assess:
        state: RelayState ->
        road: string ->
        incumbent: string ->
        assessment: string ->
        snapshot: string ->
        authority: string ->
        findings: obj ->
            obj

    val invalidateCertificate: state: RelayState -> road: string -> reason: string -> obj

    val advanceAuthority:
        state: RelayState ->
        road: string ->
        incumbent: string ->
        expected: string ->
        next: string ->
        authorityMessageId: string ->
        snapshot: string ->
            obj

    val blockCleanup: state: RelayState -> road: string -> incumbent: string -> blockerDigest: string -> obj

    val confirmRetirement:
        state: RelayState -> road: string -> incumbent: string -> providerRunId: string -> toolCallId: string -> obj

    val retireContinue:
        state: RelayState ->
        road: string ->
        incumbent: string ->
        retirement: string ->
        providerRun: string ->
        toolCall: string ->
        snapshot: string ->
            obj

    val retireAccepted:
        state: RelayState ->
        road: string ->
        incumbent: string ->
        retirement: string ->
        providerRun: string ->
        toolCall: string ->
        certificateId: string ->
        snapshot: string ->
            obj

    val view: state: RelayState -> road: string -> obj
    val authority: state: RelayState -> road: string -> obj
    val certificate: state: RelayState -> road: string -> obj
    val retirement: state: RelayState -> road: string -> obj
    val roadDevOps: state: RelayState -> road: string -> obj
    val bindRoadDevOps: state: RelayState -> road: string -> devopsId: string -> modelTarget: obj -> obj
