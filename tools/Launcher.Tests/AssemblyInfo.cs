using Xunit;

// The launcher keeps its state in statics (settings, the game server process), so tests run one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
