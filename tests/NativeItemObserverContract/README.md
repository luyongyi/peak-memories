This executable links the production `ItemEventCapture.cs` and uses HarmonyX 2.9
to patch controlled managed fixtures whose target signatures match the installed
game's audited `item-hook-signatures.json`.

It checks successful callback binding, native prefix/postfix order, lifecycle and
physical/state notifications, replay suppression, observer exception isolation,
native exceptions, return/out values, and unpatching on disposal.

These are runtime Harmony invocation contracts. They do not simulate Unity
physics or rendering and cannot establish in-game performance or complete event
coverage for a changed game build. The companion native signature audit remains
necessary when PEAK updates.

Run with `dotnet run --project tests/NativeItemObserverContract` on Windows.
