# CP_LUKE_SCENE/1 — shared logical contract

This is an explicitly custom, opt-in, **Town-only own-character observation**
contract. It is not an original ARAD message, a whole-party/peer-ready proof,
Run reattachment, an authorization credential or ACK668 success.

## Frozen logical fields

Request namespace/version: `CP_LUKE_SCENE/1`, version `1`.

| Field | Exact logical domain |
| --- | --- |
| requestId | Server-generated random 128-bit identifier, exactly 32 lowercase hexadecimal characters, not all zero. Opaque text; never infer GUID byte order. |
| characterId | uint16, 0..65535. Zero is legal; verified identity is a separate prerequisite. |
| town | uint8, 0..255. This range is not proof a resource exists or travel is legal. |
| area | uint8, 0..255; same restriction. |
| targetRevision | Positive int64, 1..9223372036854775807. An opaque server request-target revision, **not** native scene generation. |
| ttlMs | Integer 1..30000. Relative maximum client wait from first acceptance; server independently uses its original monotonic deadline. |

Reply has the same namespace/version and **requestId only**. No client-supplied
owner epoch, account, character, target, resource result, status or server generation
is trusted as authority. The server resolves all binding facts from its pending
request and the actual receiving session. The ID is correlation, not a secret.

`Tests/Extensions/LukeScene/contract-vectors.json` is the shared cross-language
fixture. Its numeric field strings preserve exact int64 values without JSON/JS
precision loss; they are canonical decimal (no signs, leading zeros, spaces,
NUL, extra bytes or alternate versions). **This JSON and the C++ field parser do
not choose a transport/framing or reserve an original opcode.** A future transport
must enforce the exact schema and explicit bilateral capability; it is absent here.

## Client invariants

1. Both local feature enable and out-of-band verified server capability default
   false. No auto-probe, inferred capability, login send or unsolicited reply.
2. The accepted request is bound to independently verified current character,
   local owner epoch, target Town/Area and local scene generation. The target scene
   identity must already be known; its objects/resources may still be loading.
   A request arriving before that binding is known is rejected, not speculatively
   carried through a scene transition. The server may later issue a **new ID**.
3. Local owner epoch and scene generation are adapter-local monotonic non-reused
   counters. Neither is sent or equated with targetRevision/server lease/raid epoch.
   Receive-side ingress binding must identify the actual delivering owner, not be
   manufactured from a later current-owner read.
4. Same-binding post-update observations must explicitly establish active target
   scene, resources ready, self Actor, self Town entity and attachment to that same
   scene. Each observation input is Unknown/NotReady/Ready; **every input must be
   Ready**. Default/unverified observer returns Unknown and can never produce a reply.
   Its monotonic sample time must be strictly after request acceptance and not in
   the future, and strictly newer than every previously seen same-binding sample,
   including NotReady/Unknown. An earlier/same-timestamp cached Ready snapshot
   cannot override a newer negative/unknown observation.
   These are verification requirements, not guessed native offsets or proved hooks.
5. Owner/character/scene change or unknown binding, gate change, expiry or monotonic
   clock rollback invalidates pending. Clock rollback locks the model closed.
   Scene generation and targetRevision are independent, even if numerically equal.
6. Duplicate ID does not replace pending or extend its first deadline. New accepted
   ID supersedes old pending. IDs accepted during this model lifetime remain retired;
   the bounded 64-ID store never evicts. Exhaustion rejects new requests rather than
   enabling replay. Reinitialization is not an automatic recovery path.
7. Reply is consumed **before** handing one attempt to a future sender. Failure,
   exception, loss or uncertain delivery is not retried with the same ID. There is
   no success claim merely because the sender was called. No reply for Unknown,
   NotReady or expiration. The server can choose a fresh request after revalidation.
8. Model methods are serialized on the eventual verified owner/UI thread; this pure
   model provides no native thread selection, locking or cross-thread hook safety.

## Server invariants

The server stores authoritative account/character, actual connection/owner lease,
selection epoch, raid instance/phase/generation, legal Town/Area/targetRevision and
original monotonic deadline with the random ID. A receipt must match that live
pending record and actual owner; late, duplicate, replaced, invalid or wrong-owner
receipts cannot advance it. Duplicate receipt never refreshes a deadline.

Receipt acceptance marks **Observed only**. Continuing requires separate one-shot
consumption plus fresh authoritative revalidation of the same facts; observation
does not bypass any guard, grant assets, teleport, install a Run or emit ACK668.
Client-local readiness is not a security attestation and cannot authorize a changed
server target. Disconnect/reselect/scene/raid changes invalidate pending server-side.

## Present implementation boundary

Only logical values, pure model and self-written fixtures exist. No opcode selected,
packet handler registered, production setting activated, DLL entry added, native
observer/sender installed, client or server guard changed, or deployment performed.
Existing CP_SCORE/1 and RuntimeApi/GameNative ABIs are untouched. No original EXE
execution, loading, emulation or injection is part of these tests.
