namespace Cantrip.Runtime
{
    // The small amount of interpreter state that belongs in a save file.
    public sealed partial class Interpreter
    {
        /// <summary>Next causal-chain id. Saved so that <c>once per chain</c> windows stay distinct after a load.</summary>
        internal long ChainCounter
        {
            get => _nextChainRoot;
            set => _nextChainRoot = value;
        }

        /// <summary>True while an action is resolving or triggers are queued: no safe point to snapshot.</summary>
        internal bool HasPendingWork => _queue.Count > 0 || _draining;
    }
}
