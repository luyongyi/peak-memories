# Cinematic effects contracts

Run `dotnet run --project tests/CinemaEffectsContract/CinemaEffectsContract.csproj -c Release` from the repository root. This directly compiles the production effects controller with small Unity substitutes and checks runtime ownership, parameter isolation, neutral settings, renderer capability degradation and independent cleanup.

Run `dotnet build tests/CinemaEffectsContract/CinemaEffectsContract.csproj -c Release -p:UnityApiCheck=true` to compile the same controller against the locally installed PEAK Unity/URP assemblies. Set `PEAKGameRootDir` if needed. This mode does not load or execute the game or redistribute game assemblies.

Neither check proves the appearance or cost of color grading or depth of field. In-game acceptance must check the actual renderer, camera depth texture, transparent objects and particles, camera stacks, paused/seek behavior, UI exclusion and restoration when cinematic mode closes. Low DOF is Gaussian background blur, Medium enables its improved sampling, and High requests Bokeh and falls back to supported Gaussian. DOF Off removes only the cinematic override and retains native environmental effects.
