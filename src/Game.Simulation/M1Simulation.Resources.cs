namespace Game.Simulation;

public sealed partial class HospitalSimulation
{
    private sealed class ResourcePool
    {
        private readonly AtomicResourcePool inner;

        public ResourcePool(params (string Id, int Capacity)[] definitions)
        {
            inner = new AtomicResourcePool(definitions);
        }

        public int NegativeResourceCount => inner.NegativeResourceCount;

        public IReadOnlyDictionary<string, int> Usage() => inner.Usage();

        public IEnumerable<(string Id, int Used, int Capacity)> CanonicalState() => inner.CanonicalState();

        public bool TryReserve(IReadOnlyList<string> resourceIds) => inner.TryReserve(resourceIds);

        public void Release(IReadOnlyList<string> resourceIds) => inner.Release(resourceIds);
    }
}
