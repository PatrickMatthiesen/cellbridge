------------------------ MODULE OfficePublication ------------------------
EXTENDS Naturals, TLC

CONSTANTS Target, Slot, RequestKey, FenceAuthority, FenceRoot
Workers == {"w1", "w2"}
Docs == {"d1", "d2"}
Keys == {"x", "y"}
None == "none"
Empty == [k \in Keys |-> None]
SameDocument == [w \in Workers |-> "d1"]
DifferentDocuments == [w \in Workers |-> IF w = "w1" THEN "d1" ELSE "d2"]
DifferentKeys == [w \in Workers |-> IF w = "w1" THEN "x" ELSE "y"]
SameKey == [w \in Workers |-> "x"]
DistinctRequests == [w \in Workers |-> w]
SharedRequest == [w \in Workers |-> "w1"]

\* Each worker is one immutable, validated request from the same actor in
\* lifecycle 0. Worker identity is also its distinct payload digest.
VARIABLES head, durable, receipts, effects, authority, lifecycle, lease,
          now, guard, phase, prepared, authSafe, rootSafe, keySafe
vars == <<head, durable, receipts, effects, authority, lifecycle, lease,
          now, guard, phase, prepared, authSafe, rootSafe, keySafe>>
Control == <<authority, lifecycle, lease, now>>
Audit == <<authSafe, rootSafe, keySafe>>
ReceiptId(w) == <<Target[w], "actor", 0, RequestKey[w]>>
ReceiptIds == {ReceiptId(w) : w \in Workers}
Authorized(w) == authority[Target[w]] /\ lifecycle[Target[w]] = 0
                 /\ lease[Target[w]] /\ now < 1
Coherent(w) == head[Target[w]][Slot[w]] = None
Fresh(w) == prepared[w] = head[Target[w]]
Candidate(w) == [prepared[w] EXCEPT ![Slot[w]] = w]

Init ==
    /\ head = [d \in Docs |-> Empty]
    /\ durable = {}
    /\ receipts = [r \in ReceiptIds |-> None]
    /\ effects = [r \in ReceiptIds |-> 0]
    /\ authority = [d \in Docs |-> TRUE]
    /\ lifecycle = [d \in Docs |-> 0]
    /\ lease = [d \in Docs |-> TRUE]
    /\ now = 0
    /\ guard = [d \in Docs |-> None]
    /\ phase = [w \in Workers |-> "new"]
    /\ prepared = [w \in Workers |-> Empty]
    /\ authSafe = TRUE /\ rootSafe = TRUE /\ keySafe = TRUE

\* Validation and payload durability finish before the row guard.
Prepare(w) ==
    /\ phase[w] = "new" /\ receipts[ReceiptId(w)] = None
    /\ Authorized(w)
    /\ durable' = durable \cup {w}
    /\ prepared' = [prepared EXCEPT ![w] = head[Target[w]]]
    /\ phase' = [phase EXCEPT ![w] = "ready"]
    /\ UNCHANGED <<head, receipts, effects, Control, guard, Audit>>

Acquire(w) ==
    /\ phase[w] = "ready" /\ guard[Target[w]] = None
    /\ guard' = [guard EXCEPT ![Target[w]] = w]
    /\ phase' = [phase EXCEPT ![w] = "locked"]
    /\ UNCHANGED <<head, durable, receipts, effects, Control, prepared, Audit>>

\* An unrelated mapping changed. Release and rebuild from the current root;
\* the client mapping precondition remains unchanged. This is not rejection.
Reprepare(w) ==
    /\ phase[w] = "locked" /\ Authorized(w) /\ Coherent(w)
    /\ receipts[ReceiptId(w)] = None /\ ~Fresh(w) /\ FenceRoot
    /\ prepared' = [prepared EXCEPT ![w] = head[Target[w]]]
    /\ phase' = [phase EXCEPT ![w] = "ready"]
    /\ guard' = [guard EXCEPT ![Target[w]] = None]
    /\ UNCHANGED <<head, durable, receipts, effects, Control, Audit>>

\* The transaction linearizes the current checks, binding changes and receipt.
\* Clock is read here, after acquisition, not cached by Prepare or Acquire.
Publish(w) ==
    /\ phase[w] = "locked" /\ guard[Target[w]] = w
    /\ receipts[ReceiptId(w)] = None /\ Coherent(w)
    /\ (FenceAuthority => Authorized(w))
    /\ (FenceRoot => Fresh(w))
    /\ head' = [head EXCEPT ![Target[w]] = Candidate(w)]
    /\ receipts' = [receipts EXCEPT ![ReceiptId(w)] = w]
    /\ effects' = [effects EXCEPT ![ReceiptId(w)] = @ + 1]
    /\ authSafe' = (authSafe /\ Authorized(w))
    /\ rootSafe' = (rootSafe /\ \A k \in Keys \ {Slot[w]} :
                        Candidate(w)[k] = head[Target[w]][k])
    /\ keySafe' = (keySafe /\ Coherent(w))
    /\ phase' = [phase EXCEPT ![w] = "unknown"]
    /\ guard' = [guard EXCEPT ![Target[w]] = None]
    /\ UNCHANGED <<durable, Control, prepared>>

\* The same actor/lifecycle/key with another digest cannot become a new save.
\* Lookup after unknown commit or crash returns the durable accepted result.
Resolve(w) ==
    /\ phase[w] \in {"new", "ready", "locked", "unknown"}
    /\ receipts[ReceiptId(w)] # None
    /\ phase' = [phase EXCEPT ![w] =
                    IF receipts[ReceiptId(w)] = w THEN "done" ELSE "rejected"]
    /\ guard' = [guard EXCEPT ![Target[w]] =
                    IF @ = w THEN None ELSE @]
    /\ UNCHANGED <<head, durable, receipts, effects, Control, prepared, Audit>>

Reject(w) ==
    /\ phase[w] = "locked" /\ receipts[ReceiptId(w)] = None
    /\ (~Authorized(w) \/ ~Coherent(w))
    /\ phase' = [phase EXCEPT ![w] = "rejected"]
    /\ guard' = [guard EXCEPT ![Target[w]] = None]
    /\ UNCHANGED <<head, durable, receipts, effects, Control, prepared, Audit>>

Cancel(w) ==
    /\ phase[w] \in {"new", "ready", "locked"}
    /\ phase' = [phase EXCEPT ![w] = "cancelled"]
    /\ guard' = [guard EXCEPT ![Target[w]] =
                    IF @ = w THEN None ELSE @]
    /\ UNCHANGED <<head, durable, receipts, effects, Control, prepared, Audit>>

\* Each control mutation takes the affected document guard atomically.
Revoke(d) ==
    /\ d \in {Target[w] : w \in Workers} /\ guard[d] = None /\ authority[d]
    /\ authority' = [authority EXCEPT ![d] = FALSE]
    /\ UNCHANGED <<head, durable, receipts, effects, lifecycle, lease, now,
                    guard, phase, prepared, Audit>>
ReplaceLifecycle(d) ==
    /\ d \in {Target[w] : w \in Workers} /\ guard[d] = None /\ lifecycle[d] = 0
    /\ lifecycle' = [lifecycle EXCEPT ![d] = 1]
    /\ UNCHANGED <<head, durable, receipts, effects, authority, lease, now,
                    guard, phase, prepared, Audit>>
RevokeLease(d) ==
    /\ d \in {Target[w] : w \in Workers} /\ guard[d] = None /\ lease[d]
    /\ lease' = [lease EXCEPT ![d] = FALSE]
    /\ UNCHANGED <<head, durable, receipts, effects, authority, lifecycle, now,
                    guard, phase, prepared, Audit>>
Tick ==
    /\ now = 0 /\ now' = 1
    /\ UNCHANGED <<head, durable, receipts, effects, authority, lifecycle,
                    lease, guard, phase, prepared, Audit>>

\* Crash destroys process state and row locks, not committed data or receipts.
Crash ==
    /\ \E w \in Workers : phase[w] \in {"ready", "locked", "unknown"}
    /\ phase' = [w \in Workers |->
          IF phase[w] \in {"ready", "locked", "unknown"} THEN "new" ELSE phase[w]]
    /\ prepared' = [w \in Workers |-> Empty]
    /\ guard' = [d \in Docs |-> None]
    /\ UNCHANGED <<head, durable, receipts, effects, Control, Audit>>

Next == (\E w \in Workers : Prepare(w) \/ Acquire(w) \/ Reprepare(w)
                \/ Publish(w) \/ Resolve(w) \/ Reject(w) \/ Cancel(w))
        \/ (\E d \in Docs : Revoke(d) \/ ReplaceLifecycle(d) \/ RevokeLease(d))
        \/ Tick \/ Crash
Spec == Init /\ [][Next]_vars

TypeOK ==
    /\ head \in [Docs -> [Keys -> Workers \cup {None}]]
    /\ durable \subseteq Workers
    /\ receipts \in [ReceiptIds -> Workers \cup {None}]
    /\ effects \in [ReceiptIds -> Nat]
    /\ authority \in [Docs -> BOOLEAN] /\ lifecycle \in [Docs -> 0..1]
    /\ lease \in [Docs -> BOOLEAN] /\ now \in 0..1
    /\ guard \in [Docs -> Workers \cup {None}]
    /\ phase \in [Workers -> {"new", "ready", "locked", "unknown", "done",
                              "rejected", "cancelled"}]
    /\ prepared \in [Workers -> [Keys -> Workers \cup {None}]]
    /\ authSafe \in BOOLEAN /\ rootSafe \in BOOLEAN /\ keySafe \in BOOLEAN
DurableHeads == \A d \in Docs, k \in Keys : head[d][k] \in durable \cup {None}
AtMostOnce == \A r \in ReceiptIds : effects[r] <= 1
ReceiptAtomicity == \A r \in ReceiptIds :
    (effects[r] = 1) <=> (receipts[r] # None)
GuardOwnership == \A d \in Docs : guard[d] # None =>
    (Target[guard[d]] = d /\ phase[guard[d]] = "locked")
IndependentGuards == \A w \in Workers :
    (phase[w] = "ready" /\ guard[Target[w]] = None) => ENABLED Acquire(w)
CurrentAuthority == authSafe
PreserveOtherMappings == rootSafe
KeywiseCoherency == keySafe
SuccessfulReceipt == \A w \in Workers :
    phase[w] = "done" => receipts[ReceiptId(w)] = w
ReceiptPayloadDurable == \A r \in ReceiptIds : receipts[r] \in durable \cup {None}
\* Expected-to-fail reachability check, not a safety requirement.
NoSimultaneousGuards == ~(guard["d1"] = "w1" /\ guard["d2"] = "w2")
=============================================================================
