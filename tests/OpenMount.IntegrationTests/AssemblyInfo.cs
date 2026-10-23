// Real mounts share drive letters and WinFsp; run the integration test classes one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
