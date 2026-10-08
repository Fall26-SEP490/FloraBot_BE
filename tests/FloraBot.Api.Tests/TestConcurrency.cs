// Each integration host runs CAP workers and database pools. Bound host concurrency
// independently of workstation CPU count; individual tests still exercise races.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
