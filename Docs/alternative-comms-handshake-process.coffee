sequenceDiagram
    participant E as Engine
    participant P as NanoService

    rect rgb(230,230,230)
    note over E,N: Phase 1 – STDIO initialisation

    E->>N: HelloSignal
    N-->>E: AcknowledgeSignal

    E->>N: SetupSignal
    N-->>E: ReadySignal
    E->>N: DataSignal(configBase64Json)
    N-->>E: AcknowledgeWithReceiptSignal(SHA256(config))

    E->>N: UpgradeCommsMethodSignal
    N-->>E: ReadySignal
    E->>N: DataSignal(inboxHandle, outboxHandle, capacities)
    N-->>E: AcknowledgeWithReceiptSignal(handleReceipt)
    E->>N: ReadyForTestSignal
    end

    rect rgb(220,245,220)
    note over E,N: Phase 2 – MMF test and upgrade
    note over E,N: Now using Inbox/Outbox MMFs for data

    N->>E: (Outbox MMF) TestSignal("Hello"+"random fill")
    E-->>N: (Inbox MMF) AcknowledgeWithReceiptSignal(SHA256(payload))
    N->>E: (Outbox MMF) ReadyForTestSignal

    E->>N: (Inbox MMF) TestSignal("Hello"+"random fill")
    N-->>E: (Outbox MMF) AcknowledgeWithReceiptSignal(SHA256(payload))
    E->>N: (Inbox MMF) ReadySignal
    N-->>E: (Outbox MMF) ReadySignal
    end
