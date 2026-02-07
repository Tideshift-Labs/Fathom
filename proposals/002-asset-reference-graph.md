# Proposal 002: Asset Reference Graph

## Problem

Every UE asset references other assets: a Blueprint references meshes, materials, and sounds it uses; a Material references textures and material functions; a Level references every placed actor. These form a directed graph with potentially thousands of edges.

UE has a Reference Viewer in the editor for humans, and the Asset Registry tracks it all internally. But an LLM agent working through text has zero visibility into any of it.

### Where this hurts agents

**Impact analysis ("what breaks if I touch this?")** is the big one. If an agent is asked to replace a material, restructure a Blueprint, or delete an apparently-unused asset, it's flying blind. It can read C++ `#include` chains, but the asset-to-asset web is completely opaque. This makes agents either overly conservative or reckless because they can't see consequences.

**Dependency understanding for context loading.** When an agent is working on `BP_PlayerCharacter`, it would benefit from knowing that this Blueprint references `ABP_PlayerAnimBP`, `SK_PlayerMesh`, `M_PlayerSkin`, and `SFX_Footstep_Cue`. That's the "neighborhood" the agent should be aware of, and it has no way to discover it today.

**Hard vs soft reference awareness.** Hard references force assets into memory at load time. If `BP_MainMenu` hard-references a 500MB texture atlas meant for gameplay, that's a real performance issue. An agent doing optimization work needs to see this, and the distinction between hard and soft references matters.

**Orphan detection.** Assets with zero referencers are potential dead content. Not always (some are loaded by string path, or are top-level maps), but it's a useful signal.

## What UE gives us to work with

The **Asset Registry** (`IAssetRegistry`) is the authoritative source. It maintains:

- `GetDependencies(AssetId)` - what does this asset depend on?
- `GetReferencers(AssetId)` - what depends on this asset?
- `GetAllAssets()` - enumerate everything

It also distinguishes dependency types: hard, soft, searchable name, and manage (cooking-only). This classification is already there; we just need to surface it.

The registry data lives in memory when the editor is running and is also cached to `Saved/` as binary files between sessions.

## Use cases for LLM agents

- "What does BP_Player depend on?" - list all assets it pulls in
- "What uses this material?" - find all referencers before modifying or deleting
- "Is it safe to delete this texture?" - check for zero referencers
- "Why is this level so large?" - trace hard reference chains to find heavy transitive deps
- "Show me circular dependencies" - detect cycles that cause loading issues
- "I want to move this asset" - identify everything that needs updating

## Possible solutions

### A: On-demand queries to a live UE editor

The CoRider-UnrealEngine plugin runs an HTTP listener inside the editor subsystem. The Rider plugin proxies `/asset-refs` requests to it.

**Pros:**
- Always fresh, no staleness problem at all
- No JSON files on disk, no refresh cycles
- Simple UE-side implementation (just call `GetDependencies` / `GetReferencers`)

**Cons:**
- Requires the UE editor to be running. If it's closed, agents lose access entirely. This is a real limitation since LLM agents may work on a project while only Rider is open.

### B: Batch export to JSON (like Blueprint audit)

A commandlet dumps the full asset graph to a JSON file. The Rider plugin reads and serves it, using staleness detection similar to Blueprint audit.

**Pros:**
- Available even when the UE editor is not running
- Proven pattern (Blueprint audit already works this way)

**Cons:**
- Staleness is inherent. Graph changes whenever assets are added, deleted, or re-saved
- Full graph JSON could be several MB on large projects
- Requires a refresh cycle and 409 handling

### C: Cached queryable graph (hybrid)

Export the graph to a structured format that the Rider plugin loads into memory and can query efficiently. Could be JSON, SQLite, or a simple adjacency list. When the UE editor is available, refresh incrementally; when it's not, serve from cache.

**Pros:**
- Available when editor is closed (agents still have context)
- Can be queried without loading the entire graph into an LLM context window
- Rider plugin can compute derived analysis (cycles, orphans, heaviest nodes) on the cached graph
- Incremental refresh possible if the UE editor is running

**Cons:**
- More moving parts than either pure approach
- Cache invalidation is the usual headache
- Need to decide on storage format

### D: Parse Asset Registry binary cache directly

UE writes `DevelopmentAssetRegistry.bin` to `Saved/`. The Rider plugin could parse this directly without needing the UE editor.

**Pros:**
- No commandlet needed, no editor needed
- File already exists as part of normal UE workflow

**Cons:**
- Binary format is internal to UE and may change between engine versions
- Would need reverse-engineering or a maintained parser
- Fragile across UE updates

## Scoping decisions (deferred)

- **Engine content:** Include references *to* engine assets? Probably yes for outgoing edges (useful context) but not as nodes with their own deps (too noisy).
- **Graph size:** Large projects could have 10k+ assets. May need filtered views (by path prefix, by asset type).
- **Update frequency:** Asset graph changes less frequently than Blueprint internals, but still needs a refresh mechanism.
- **Derived analysis:** Cycle detection, orphan detection, hard-reference chain analysis, heaviest nodes. These could live in the Rider plugin regardless of which data approach is chosen.

## How this pairs with other proposals

The [delegate binding map](001-delegate-binding-map.md) shows the *code-level* implicit wiring. The asset reference graph shows the *content-level* implicit wiring. Together, an agent can answer both "what code reacts when this event fires?" and "what content is affected if I change this asset?" Those are the two big blind spots for text-based agents working on UE projects.
