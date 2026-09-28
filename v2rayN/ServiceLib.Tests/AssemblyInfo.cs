using Xunit;

// Configuration generators share AppManager.Instance and the process-wide SQLite connection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
