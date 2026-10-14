int passed=0;
_ = new PeakReplayLab.ReplayHeader { CoverOutcome = null };
void Test(string name,Action test)
{
    try {test();passed++;Console.WriteLine("PASS "+name);}
    catch(Exception error){Console.Error.WriteLine("FAIL "+name+": "+error);Environment.Exit(1);}
}
ReplayTrackSharedBudgetTests.Run(Test);
Console.WriteLine($"TOTAL: {passed} shared rolling-track budget contracts passed. No Unity runtime is simulated.");
