using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Array = System.Array;
using VMArray = Neo.VM.Types.Array;

namespace Neo.SmartContract
{
    public partial class ApplicationEngine : ExecutionEngine
    {
        public static event EventHandler<NotifyEventArgs> Notify;
        public static event EventHandler<LogEventArgs> Log;

        public const long GasFree = 0;

        private static Dictionary<uint, InteropDescriptor> services;
        private readonly long gas_amount;
        private readonly bool testMode;
        private readonly List<NotifyEventArgs> notifications = new List<NotifyEventArgs>();
        private readonly List<IDisposable> disposables = new List<IDisposable>();
        private readonly Dictionary<UInt160, int> invocationCounter = new Dictionary<UInt160, int>();

        public static IEnumerable<InteropDescriptor> Services => services.Values;
        public TriggerType Trigger { get; }
        public IVerifiable ScriptContainer { get; }
        public StoreView Snapshot { get; }
        public long GasConsumed { get; private set; } = 0;
        public long GasLeft => testMode ? -1 : gas_amount - GasConsumed;
        public UInt160 CurrentScriptHash => CurrentContext?.GetState<ExecutionContextState>().ScriptHash;
        public UInt160 CallingScriptHash => CurrentContext?.GetState<ExecutionContextState>().CallingScriptHash;
        public UInt160 EntryScriptHash => EntryContext?.GetState<ExecutionContextState>().ScriptHash;
        public IReadOnlyList<NotifyEventArgs> Notifications => notifications;

        public ApplicationEngine(TriggerType trigger, IVerifiable container, StoreView snapshot, long gas, bool testMode = false)
        {
            this.gas_amount = GasFree + gas;
            this.testMode = testMode;
            this.Trigger = trigger;
            this.ScriptContainer = container;
            this.Snapshot = snapshot;
        }

        internal bool AddGas(long gas)
        {
            GasConsumed = checked(GasConsumed + gas);
            return testMode || GasConsumed <= gas_amount;
        }

        protected override void ContextUnloaded(ExecutionContext context)
        {
            base.ContextUnloaded(context);
            if (context.EvaluationStack == CurrentContext?.EvaluationStack) return;
            int rvcount = context.GetState<ExecutionContextState>().RVCount;
            if (rvcount != -1 && rvcount != context.EvaluationStack.Count)
                throw new InvalidOperationException();
        }

        protected override void LoadContext(ExecutionContext context)
        {
            // Set default execution context state

            context.GetState<ExecutionContextState>().ScriptHash ??= ((byte[])context.Script).ToScriptHash();

            base.LoadContext(context);
        }

        public ExecutionContext LoadScript(Script script, CallFlags callFlags)
        {
            ExecutionContext context = LoadScript(script);
            context.GetState<ExecutionContextState>().CallFlags = callFlags;
            return context;
        }

        internal StackItem Convert(object value)
        {
            return value switch
            {
                null => StackItem.Null,
                bool b => b,
                sbyte i => i,
                byte i => (BigInteger)i,
                short i => i,
                ushort i => (BigInteger)i,
                int i => i,
                uint i => i,
                long i => i,
                ulong i => i,
                Enum e => Convert(System.Convert.ChangeType(e, e.GetTypeCode())),
                byte[] data => data,
                string s => s,
                BigInteger i => i,
                IInteroperable interoperable => interoperable.ToStackItem(ReferenceCounter),
                ISerializable i => i.ToArray(),
                StackItem item => item,
                (object a, object b) => new Struct(ReferenceCounter) { Convert(a), Convert(b) },
                Array array => new VMArray(ReferenceCounter, array.OfType<object>().Select(p => Convert(p))),
                _ => StackItem.FromInterface(value)
            };
        }

        internal object Convert(StackItem item, InteropParameterDescriptor descriptor)
        {
            if (descriptor.IsArray)
            {
                Array av;
                if (item is VMArray array)
                {
                    av = Array.CreateInstance(descriptor.Type.GetElementType(), array.Count);
                    for (int i = 0; i < av.Length; i++)
                        av.SetValue(descriptor.Converter(array[i]), i);
                }
                else
                {
                    int count = (int)item.GetBigInteger();
                    if (count > MaxStackSize) throw new InvalidOperationException();
                    av = Array.CreateInstance(descriptor.Type.GetElementType(), count);
                    for (int i = 0; i < av.Length; i++)
                        av.SetValue(descriptor.Converter(Pop()), i);
                }
                return av;
            }
            else
            {
                object value = descriptor.Converter(item);
                if (descriptor.IsEnum)
                    value = Enum.ToObject(descriptor.Type, value);
                else if (descriptor.IsInterface)
                    value = ((InteropInterface)value).GetInterface<object>();
                return value;
            }
        }

        public override void Dispose()
        {
            foreach (IDisposable disposable in disposables)
                disposable.Dispose();
            disposables.Clear();
            base.Dispose();
        }

        protected override bool OnSysCall(uint method)
        {
            if (!services.TryGetValue(method, out InteropDescriptor descriptor))
                return false;
            if (!descriptor.AllowedTriggers.HasFlag(Trigger))
                return false;
            ExecutionContextState state = CurrentContext.GetState<ExecutionContextState>();
            if (!state.CallFlags.HasFlag(descriptor.RequiredCallFlags))
                return false;
            if (!AddGas(descriptor.FixedPrice))
                return false;
            List<object> parameters = descriptor.Parameters.Length > 0
                ? new List<object>()
                : null;
            foreach (var pd in descriptor.Parameters)
                parameters.Add(Convert(Pop(), pd));
            object returnValue = descriptor.Handler.Invoke(this, parameters?.ToArray());
            if (descriptor.Handler.ReturnType != typeof(void))
                Push(Convert(returnValue));
            return true;
        }

        protected override bool PreExecuteInstruction()
        {
            if (CurrentContext.InstructionPointer >= CurrentContext.Script.Length)
                return true;
            return AddGas(OpCodePrices[CurrentContext.CurrentInstruction.OpCode]);
        }

        private static Block CreateDummyBlock(StoreView snapshot)
        {
            var currentBlock = snapshot.Blocks[snapshot.CurrentBlockHash];
            return new Block
            {
                Version = 0,
                PrevHash = snapshot.CurrentBlockHash,
                MerkleRoot = new UInt256(),
                Timestamp = currentBlock.Timestamp + Blockchain.MillisecondsPerBlock,
                Index = snapshot.Height + 1,
                NextConsensus = currentBlock.NextConsensus,
                Witness = new Witness
                {
                    InvocationScript = Array.Empty<byte>(),
                    VerificationScript = Array.Empty<byte>()
                },
                ConsensusData = new ConsensusData(),
                Transactions = new Transaction[0]
            };
        }

        private static InteropDescriptor Register(string name, string handler, long fixedPrice, TriggerType allowedTriggers, CallFlags requiredCallFlags)
        {
            MethodInfo method = typeof(ApplicationEngine).GetMethod(handler, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? typeof(ApplicationEngine).GetProperty(handler, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetMethod;
            InteropDescriptor descriptor = new InteropDescriptor(name, method, fixedPrice, allowedTriggers, requiredCallFlags);
            services ??= new Dictionary<uint, InteropDescriptor>();
            services.Add(descriptor.Hash, descriptor);
            return descriptor;
        }

        public static ApplicationEngine Run(byte[] script, StoreView snapshot,
            IVerifiable container = null, Block persistingBlock = null, int offset = 0, bool testMode = false, long extraGAS = default)
        {
            snapshot.PersistingBlock = persistingBlock ?? snapshot.PersistingBlock ?? CreateDummyBlock(snapshot);
            ApplicationEngine engine = new ApplicationEngine(TriggerType.Application, container, snapshot, extraGAS, testMode);
            engine.LoadScript(script).InstructionPointer = offset;
            engine.Execute();
            return engine;
        }

        public static ApplicationEngine Run(byte[] script, IVerifiable container = null, Block persistingBlock = null, int offset = 0, bool testMode = false, long extraGAS = default)
        {
            using (SnapshotView snapshot = Blockchain.Singleton.GetSnapshot())
            {
                return Run(script, snapshot, container, persistingBlock, offset, testMode, extraGAS);
            }
        }

        public bool TryPop(out string s)
        {
            if (TryPop(out ReadOnlySpan<byte> b))
            {
                s = Encoding.UTF8.GetString(b);
                return true;
            }
            else
            {
                s = default;
                return false;
            }
        }
    }
}
