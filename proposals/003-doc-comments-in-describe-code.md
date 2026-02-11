# Proposal 003: Doc Comments in /describe_code

## Problem

The `/describe_code` endpoint returns structural information (types, members, signatures, modifiers) but omits documentation comments entirely. When an LLM agent asks "what does this method do?", the response contains the method's name and signature but not the author's own description of its purpose, behavior, or parameter semantics.

For well-documented codebases this is a significant loss. XML doc comments in C# and Doxygen-style comments in C++ are often the fastest path to understanding intent without reading the full implementation.

## Proposed change

Add an optional `Summary` field to `TypeInfo` and `MemberInfo` in `CodeStructureModels.cs`. When a type or member has a doc comment, populate this field with the extracted summary text. When absent, omit it from the response (matching the existing nullable-field convention).

## What gets extracted

### C#

XML doc comments attached to declarations:

```csharp
/// <summary>
/// Spawns a projectile at the given origin and direction.
/// </summary>
/// <param name="origin">World-space spawn point.</param>
/// <param name="direction">Normalized launch direction.</param>
public void FireProjectile(Vector3 origin, Vector3 direction) { ... }
```

Extract the `<summary>` content as a plain-text string. Parameter-level `<param>` docs could optionally populate a `Description` field on `ParameterInfo`.

### C++

Doxygen-style comments preceding declarations:

```cpp
/// Spawns a projectile at the given origin and direction.
/// @param Origin  World-space spawn point.
/// @param Direction  Normalized launch direction.
UFUNCTION(BlueprintCallable)
void FireProjectile(FVector Origin, FVector Direction);
```

Also the block form:

```cpp
/**
 * Spawns a projectile at the given origin and direction.
 */
```

Extract the leading description text (before any `@param`/`@return` tags).

## Output examples

### JSON

```json
{
  "kind": "method",
  "name": "FireProjectile",
  "summary": "Spawns a projectile at the given origin and direction.",
  "access": "public",
  "returnType": "void",
  "parameters": [
    {"name": "origin", "type": "Vector3"},
    {"name": "direction", "type": "Vector3"}
  ]
}
```

### Markdown

```
#### `public void FireProjectile(Vector3 origin, Vector3 direction)` (line 42)

Spawns a projectile at the given origin and direction.
```

## Implementation

### Model changes (`CodeStructureModels.cs`)

Add to `TypeInfo` and `MemberInfo`:

```csharp
[JsonPropertyName("summary")]
public string Summary { get; set; }
```

Nullable, omitted from JSON when null (existing convention).

### C# walker (`CSharpStructureWalker.cs`)

ReSharper's PSI provides direct access to XML doc comments on any `IDeclaration`:

- `IDocCommentBlock` can be obtained from the declaration's tree node
- Parse the `<summary>` element and strip XML tags to produce plain text
- Estimated addition: ~10-15 lines

### C++ walker (`CppStructureWalker.cs`)

The C++ walker already uses reflection and tree-node inspection. Doc comments appear as sibling or child nodes preceding the declaration:

- Look for `IDocCommentNode` or comment-type tree nodes immediately before a declaration
- Strip leading `///`, `/**`, `*/`, and `*` prefixes
- Strip Doxygen tags (`@param`, `@return`, `@brief`) to isolate the summary
- Estimated addition: ~15-20 lines, slightly more involved due to the reflection-based approach

### Markdown formatter (`DescribeCodeMarkdownFormatter.cs`)

After rendering the member/type signature line, append the summary as a paragraph if present:

```csharp
if (!string.IsNullOrEmpty(member.Summary))
    sb.AppendLine().AppendLine(member.Summary);
```

Estimated addition: ~3-5 lines per render method (types and members).

## Scope decisions

| Scope item | Include? | Rationale |
|---|---|---|
| `<summary>` / leading description | Yes | Highest value, lowest cost |
| `<param>` descriptions on ParameterInfo | Deferred | Useful but adds model complexity; revisit after initial version |
| `<returns>` / `@return` | Deferred | Same rationale |
| `<remarks>` / extended descriptions | No | Too verbose for structural overview |
| Inline comments within method bodies | No | Out of scope; these describe implementation, not interface |

## Known limitations

- Comments that are not directly attached to a declaration (e.g., floating comments between members) will not be captured. This is intentional since they lack a clear owner.
- The C++ reflection-based walker may need special handling for comments separated from the declaration by UE macros (`UFUNCTION`, `UPROPERTY`). The comment may attach to the macro node rather than the declaration itself.
- Inherited doc comments (`<inheritdoc />`) would require resolving the base type's documentation, which is out of scope for the initial version.
