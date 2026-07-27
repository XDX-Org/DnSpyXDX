# NetCoreDbg IL-breakpoint extension

DnSpyXDX uses a small DAP extension for decompiled-code breakpoints. Stock NetCoreDbg does not
support this contract and is never treated as if it does.

## Capability negotiation

The adapter adds this property to its `initialize` response:

```json
{
  "supportsXdxIlBreakpoints": true
}
```

DnSpyXDX sends `xdx/setIlBreakpoints` only when this value is exactly `true`.

## Request

`xdx/setIlBreakpoints` replaces the complete IL-breakpoint set for the session. Breakpoint IDs
are client-generated UUIDs and remain stable across rebinding.

```json
{
  "breakpoints": [
    {
      "id": "60f106e5-47ef-4ae4-b56d-acfc4ca2f497",
      "moduleMvid": "11111111-2222-3333-4444-555555555555",
      "methodToken": 100663297,
      "ilOffset": 4,
      "enabled": true,
      "condition": null,
      "hitCondition": null,
      "logMessage": null
    }
  ]
}
```

An empty array removes all IL breakpoints. `methodToken` must be a positive MethodDef token and
`ilOffset` must be non-negative. Disabled breakpoints remain in client state but must not be
activated by the adapter.

Initial breakpoints are sent after `initialized` and before `configurationDone`, preventing a
newly launched target from running past them.

## Response

Response order is not significant. It must contain one unique result for every requested ID.

```json
{
  "breakpoints": [
    {
      "id": "60f106e5-47ef-4ae4-b56d-acfc4ca2f497",
      "verified": true,
      "moduleMvid": "11111111-2222-3333-4444-555555555555",
      "methodToken": 100663297,
      "ilOffset": 4,
      "message": null
    }
  ]
}
```

`verified: false` represents pending, disabled, or rejected bindings. `message` explains the
state. A verified result may report a moved `ilOffset`; this becomes the binding's actual runtime
location.

## Runtime locations

Extended adapters may add `xdxLocation` to `stopped` event bodies and DAP stack frames:

```json
{
  "xdxLocation": {
    "moduleMvid": "11111111-2222-3333-4444-555555555555",
    "methodToken": 100663297,
    "ilOffset": 4
  }
}
```

DnSpyXDX maps this identity back to the smallest containing decompiler sequence point.

## Implementation status

Client negotiation, validation, request/response translation, pre-`configurationDone`
configuration, runtime-location parsing, and protocol integration tests are implemented.
The repository test adapter implements the contract.

Official stock NetCoreDbg still lacks the native `ICorDebugFunctionBreakpoint` backend. A pinned
DnSpyXDX fork and packaged binaries are required before real decompiled breakpoints can verify.
