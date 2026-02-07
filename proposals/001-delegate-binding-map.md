# Proposal 001: Delegate Binding Map

## Problem

UE's event-driven patterns decouple systems intentionally, but that decoupling makes the connections invisible to a text-based LLM agent. Consider:

```cpp
// GameMode.h -- declaration
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnPlayerDied, APlayerController*, Player);

UPROPERTY(BlueprintAssignable)
FOnPlayerDied OnPlayerDied;
```

```cpp
// GameMode.cpp:120 -- broadcast
OnPlayerDied.Broadcast(DeadPlayer);
```

```cpp
// HUDWidget.cpp:45 -- binding (different system entirely)
GameMode->OnPlayerDied.AddDynamic(this, &UHUDWidget::HandlePlayerDied);
```

```cpp
// AudioManager.cpp:30 -- another binding (yet another system)
GameMode->OnPlayerDied.AddDynamic(this, &UAudioManager::PlayDeathSound);
```

An LLM reading `GameMode.h` has no idea who listens. An LLM reading `HUDWidget.cpp` doesn't know what *else* listens. If you ask an agent "what happens when a player dies?", it has to find all three locations across the codebase, and it doesn't even know to look for `AudioManager`.

This is the implicit wiring problem. The whole point of delegates is decoupling, but that means the coupling is scattered and implicit.

## What the map would contain

Three pieces per delegate:

1. **Declaration** - where the delegate type is declared, its signature, which UPROPERTYs hold instances of it
2. **Broadcasts** - every site that calls `.Broadcast()` / `.Execute()` / `.ExecuteIfBound()`
3. **Bindings** - every site that calls `.AddDynamic()`, `.AddUObject()`, `.BindLambda()`, etc., and what handler it points to

Plus the Blueprint equivalent: Event Dispatcher declarations, Bind nodes, and Call nodes.

## Output schema

```json
{
  "delegates": [
    {
      "name": "FOnPlayerDied",
      "declaredIn": "GameMode.h:12",
      "type": "DynamicMulticast",
      "params": [{"type": "APlayerController*", "name": "Player"}],
      "properties": [{
        "owner": "AMyGameMode",
        "field": "OnPlayerDied",
        "specifiers": ["BlueprintAssignable"]
      }],
      "broadcasts": [
        {"file": "GameMode.cpp:120", "function": "AMyGameMode::HandlePlayerDeath"}
      ],
      "bindings": [
        {"file": "HUDWidget.cpp:45", "subscriber": "UHUDWidget",
         "handler": "HandlePlayerDied", "method": "AddDynamic"},
        {"file": "AudioManager.cpp:30", "subscriber": "UAudioManager",
         "handler": "PlayDeathSound", "method": "AddDynamic"}
      ],
      "blueprintBindings": [
        {"blueprint": "BP_ScoreTracker", "node": "Bind Event to OnPlayerDied",
         "handler": "OnPlayerDied_Event"}
      ]
    }
  ]
}
```

## Implementation

### C++ side (pattern scanning in Rider plugin)

The binding patterns are highly mechanical and grep-friendly:

| What | Pattern to find |
|---|---|
| Declarations | `DECLARE_*DELEGATE*` macros |
| Properties | `UPROPERTY` fields whose type starts with `F` and matches a known delegate type |
| Broadcasts | `.Broadcast(`, `.Execute(`, `.ExecuteIfBound(` |
| Bindings | `.AddDynamic(`, `.AddUObject(`, `.BindUObject(`, `.BindRaw(`, `.Add(`, `.AddLambda(`, `.BindLambda(`, `.AddSP(` |

Two implementation options in CoRider (Rider plugin):

1. **ReSharper PSI (rich, accurate)** - Use the C++ PSI tree to resolve symbols. Find all delegate type declarations, then use "find usages" style APIs on the property/field to locate bindings and broadcasts. This gives precise results with resolved types, but is more complex to implement and depends on ReSharper's C++ engine fully indexing the project.

2. **Text-based scanning (simpler, robust)** - Regex scan all `.h`/`.cpp` files for the patterns above. Less precise (matching text, not resolved symbols), but fast to implement and surprisingly effective because UE's macro patterns are very uniform. Could run through the existing `/files` infrastructure.

Recommendation: **option 2 first** as a pragmatic starting point. The macro patterns are distinctive enough that false positives are rare, and it avoids deep coupling to the C++ PSI APIs.

### Blueprint side (UE plugin extension)

The existing Blueprint audit already walks `UBlueprint` assets. Extend it to also extract:

- **Event Dispatcher declarations** - `UBlueprint::DelegateSignatureGraphs` or iterating the class's delegate properties via UHT reflection
- **Bind nodes** - Walk the event/function graphs looking for `UK2Node_AssignDelegate` and similar nodes
- **Call nodes** - Look for `UK2Node_CallDelegate` nodes

This fits naturally into the existing `FBlueprintAuditor` pass. Each dispatcher becomes an entry in the audit JSON, with its bindings being references from other Blueprints' graphs.

### Connecting the two sides

A C++ delegate might be *declared* in C++ but *bound* in Blueprint (via `BlueprintAssignable`), or vice versa. The Rider plugin would need to merge the two data sources:

1. C++ scan produces delegate declarations + C++ bindings/broadcasts
2. Blueprint audit produces Blueprint dispatcher declarations + BP bindings
3. Cross-reference: any C++ delegate with `BlueprintAssignable`/`BlueprintCallable` specifiers gets its Blueprint bindings merged in

## Known limitations

- **Lambda bindings** - `.AddLambda([this](...)` gives you the binding site but the handler is anonymous. Can report file/line but there's no named function to point to.
- **Indirect bindings** - If a delegate pointer is passed around before binding, text scanning loses track. PSI analysis handles this better but still not perfectly.
- **Runtime-only bindings** - Some bindings only happen conditionally at runtime. Static analysis sees the code path but can't say definitively "this binding is active."
- **Engine delegates** - Things like `FCoreUObjectDelegates::PostLoadMapWithWorld` are declared in engine code. Need to decide whether to scan engine source or just index project-side bindings to engine delegates.

None of these are blockers; they're places where the map is incomplete. An incomplete map is still far more useful than no map.

## Use cases for LLM agents

- "What happens when X fires?" - follow the broadcast to all subscribers
- "Is it safe to remove this handler?" - check if anything else covers the same responsibility
- "Add a new listener for OnPlayerDied" - see existing binding patterns to follow the same style
- "Why does system A react to system B?" - trace the delegate chain connecting them
- Impact analysis for refactoring delegate signatures
