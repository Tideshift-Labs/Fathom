## High Value (structural understanding)

Asset reference graph - Which assets reference which others. LLMs can't see the Reference Viewer. Knowing "this material is used by 40 meshes across 6 levels" is critical for safe refactoring and impact analysis.         
Gameplay framework wiring - Which GameMode uses which PlayerController, which Pawn, which HUD class, etc. This is configured in class defaults and ini files, not readable from C++ alone. An agent asked to "add a new
player ability" needs to know where the pieces connect.

Build module graph - .Build.cs module dependencies, plugin list from .uproject, and Target.cs configs. When an agent adds a #include, it needs to know if the module dependency exists. This is one of the most common UE
compilation errors.

Config/INI surface - DefaultGame.ini, DefaultInput.ini, DefaultEngine.ini. Input action mappings, project settings, feature flags. These heavily influence runtime behavior but live outside code.

## Medium Value (domain-specific context)

Data Tables and Data Assets - Row struct types, row names, rough shape of data-driven systems. An agent working on a weapon system needs to know stats live in a DataTable, not in C++.

Animation state machines - Anim BP state names, transition rules, montage slots, anim notifies. Animation systems are almost entirely visual and otherwise opaque to text-based agents.

AI trees - Behavior tree structure, blackboard key names/types, EQS query names. Same problem as animation: heavily visual, critical for gameplay work.

Widget hierarchy - UMG widget trees, property bindings, navigation flow. UI work is common and the structure is invisible from C++ headers.

## Lower (but still useful)

- Collision profiles and channels - custom object channels, preset responses
- Gameplay Tags hierarchy - if using GAS, the tag tree is essential context
- Level composition - high-level actor census per map (not every transform, just "Level_01 has 3 BP_EnemySpawners, 1 BP_BossArena, etc.")
- Delegate binding map - which multicast delegates are bound to what, especially cross-system wiring
