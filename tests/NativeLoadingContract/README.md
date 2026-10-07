# Native loading lifecycle contract

Run `dotnet run --project PeakReplayLab/tests/NativeLoadingContract/NativeLoadingContract.csproj -c Release`.

This links the production `NativeReplayLoading.cs` against deliberately small doubles. It verifies gate order, coroutine/screen ownership, cancellation, failure cleanup and preservation of another load or user volume. It does **not** test Unity's real coroutine scheduler, scene initialization, visual animation, scaled audio fades or offline spawning.

The production integration uses the locally audited `LoadingScreenHandler.LoadingRoutine(type, runAfter, disableMessageQueue, processes)` with its exact signature. This is the same native implementation called by `LoadWithoutDisablingQueue`, but retains the returned coroutine handle so recovery never stops every coroutine on the shared handler. A changed/missing signature is rejected before taking loader ownership.

In-game checks still required: open a memory from Title, cancel while opening, exit an active replay, verify both native loading screens finish, confirm audio is audible, and verify normal single-player plus the old footprint mod work after returning. Test plugin disable during loading as well; `ReplayRecovery` must complete the guarded return.
