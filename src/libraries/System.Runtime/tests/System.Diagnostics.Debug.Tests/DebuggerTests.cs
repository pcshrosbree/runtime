// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;

namespace System.Diagnostics.Tests
{
    public class DebuggerTests
    {
        [Fact]
        public void IsAttached()
        {
            bool b = Debugger.IsAttached;
        }

        [Fact]
        public void IsLogging()
        {
            if (Debugger.IsAttached)
                Debugger.IsLogging();
            else
                Assert.False(Debugger.IsLogging());
        }

        [Fact]
        public void Log()
        {
            Debugger.Log(10, "category", "This is a test log message raised in the System.Diagnostics.Debug tests for the .NET Debugger class.");
        }

        [Fact]
        public void NotifyOfCrossThreadDependency()
        {
            Debugger.NotifyOfCrossThreadDependency();
        }

        [Fact]
        public void Annotate_NullAnnotation_ThrowsArgumentNullException()
        {
            AssertExtensions.Throws<ArgumentNullException>("annotation", () => Debugger.Annotate(null!, new Exception()));
        }

        [Fact]
        public void Annotate_NullException_ThrowsArgumentNullException()
        {
            AssertExtensions.Throws<ArgumentNullException>("exception", () => Debugger.Annotate("annotation", null!));
        }

        [Fact]
        public void Annotate_DoesNotThrow()
        {
            Debugger.Annotate("annotation", new InvalidOperationException());
        }
    }

    // Tests of the store behind Debugger.Annotate. Debuggers read its fields by name without running code
    // (docs/design/features/debugger-annotations.md), so these tests pin its shape through reflection, and
    // read and write its thread-static fields through reflection, which acts on the calling thread. Each
    // test that touches the fields runs on a thread of its own, so that no state is shared between tests.
    public class DebuggerAnnotationStoreTests
    {
        private const string StoreTypeName = "System.Diagnostics.DebuggerAnnotationStore";
        private const int ContractRingSize = 8;

        public static bool IsStoreReflectable => PlatformDetection.IsNotBuiltWithAggressiveTrimming;

        public static bool IsStoreReflectableAndDetached => IsStoreReflectable && PlatformDetection.IsMultithreadingSupported && !Debugger.IsAttached;

        public static bool IsStoreReflectableWithThreads => IsStoreReflectable && PlatformDetection.IsMultithreadingSupported;

        // The allocation figures depend on how the runtime lays out thread-static storage, so they are
        // asserted on CoreCLR only.
        public static bool CanMeasureDetachedAllocation => PlatformDetection.IsCoreCLR && PlatformDetection.IsMultithreadingSupported && !Debugger.IsAttached;

        private static Type StoreType => typeof(Debugger).Assembly.GetType(StoreTypeName, throwOnError: true)!;

        private static Type EntryType => StoreType.GetNestedType("Entry", BindingFlags.NonPublic)!;

        private static FieldInfo AnnotationsField => StoreType.GetField("t_annotations", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static FieldInfo CountField => StoreType.GetField("t_annotationCount", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static Array? GetRing() => (Array?)AnnotationsField.GetValue(null);

        private static int GetCount() => (int)CountField.GetValue(null)!;

        private static void SetCount(int value) => CountField.SetValue(null, value);

        private static void Record(object value, Exception? exception)
        {
            MethodInfo record = StoreType.GetMethod("Record", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(object), typeof(Exception) })!;
            try
            {
                record.Invoke(null, new object?[] { value, exception });
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(e.InnerException);
            }
        }

        private static object? GetEntryValue(object entry) => EntryType.GetField("Value", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(entry);

        private static object? GetEntryException(object entry) => EntryType.GetField("Exception", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(entry);

        private static void RunOnNewThread(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            thread.Start();
            thread.Join();
            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
        }

        // The contract: the full type name, both thread-static fields with their exact types, the nested
        // Entry type and its two fields. A debugger that finds a different shape ignores the store.
        [ConditionalFact(nameof(IsStoreReflectable))]
        public void Store_HasContractShape()
        {
            Type store = StoreType;
            Assert.Equal(StoreTypeName, store.FullName);
            Assert.True(store.IsAbstract && store.IsSealed, "The holder must be a static class.");
            Assert.False(store.IsGenericTypeDefinition);

            Type entry = EntryType;
            Assert.NotNull(entry);
            Assert.True(entry.IsSealed);
            Assert.Same(store, entry.DeclaringType);

            FieldInfo annotations = AnnotationsField;
            Assert.NotNull(annotations);
            Assert.True(annotations.IsStatic);
            Assert.Equal(entry.MakeArrayType(), annotations.FieldType);
            Assert.NotNull(annotations.GetCustomAttribute<ThreadStaticAttribute>());

            FieldInfo count = CountField;
            Assert.NotNull(count);
            Assert.True(count.IsStatic);
            Assert.Equal(typeof(int), count.FieldType);
            Assert.NotNull(count.GetCustomAttribute<ThreadStaticAttribute>());

            // No other static field: any other static would need an initializer to be useful, and an
            // initializer emits a static constructor. Literal constants emit none, so they are allowed.
            string[] staticFields = Array.ConvertAll(
                Array.FindAll(store.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly), f => !f.IsLiteral),
                f => f.Name);
            Array.Sort(staticFields, StringComparer.Ordinal);
            Assert.Equal(new[] { "t_annotationCount", "t_annotations" }, staticFields);

            FieldInfo[] entryFields = entry.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Array.Sort(entryFields, (x, y) => string.CompareOrdinal(x.Name, y.Name));
            Assert.Equal(2, entryFields.Length);
            Assert.Equal("Exception", entryFields[0].Name);
            Assert.Equal(typeof(Exception), entryFields[0].FieldType);
            Assert.False(entryFields[0].IsStatic);
            Assert.True(entryFields[0].IsInitOnly);
            Assert.Equal("Value", entryFields[1].Name);
            Assert.Equal(typeof(object), entryFields[1].FieldType);
            Assert.False(entryFields[1].IsStatic);
            Assert.True(entryFields[1].IsInitOnly);
        }

        // A debugger ignores a holder that has a static constructor, because a direct read of its statics
        // could then see the state from before the constructor ran.
        [ConditionalFact(nameof(IsStoreReflectable))]
        public void Store_HasNoStaticConstructor()
        {
            Assert.Null(StoreType.TypeInitializer);
            Assert.Null(EntryType.TypeInitializer);
        }

        [ConditionalFact(nameof(IsStoreReflectableAndDetached))]
        public void Annotate_NoDebugger_RecordsNothing()
        {
            RunOnNewThread(() =>
            {
                Debugger.Annotate("annotation", new Exception());

                Assert.Null(GetRing());
                Assert.Equal(0, GetCount());
            });
        }

        [ConditionalFact(nameof(IsStoreReflectableAndDetached))]
        public void Annotate_NoDebugger_ClearsEntriesRecordedEarlier()
        {
            RunOnNewThread(() =>
            {
                // As if a debugger had been attached for one call and then detached.
                Record("recorded while attached", new Exception());
                Assert.NotNull(GetRing());

                Debugger.Annotate("annotation", new Exception());

                Assert.Null(GetRing());
                Assert.Equal(1, GetCount());
            });
        }

        // A count that has wrapped to exactly 0 skips the clear, by design: the count does not advance while
        // detached, and the state needs 2^32 entries on one thread. This pins the decision.
        [ConditionalFact(nameof(IsStoreReflectableAndDetached))]
        public void Annotate_NoDebugger_CountWrappedToZero_KeepsRing()
        {
            RunOnNewThread(() =>
            {
                SetCount(-1);
                Record("last before the wrap", new Exception());
                Assert.Equal(0, GetCount());

                Debugger.Annotate("annotation", new Exception());

                Assert.NotNull(GetRing());
            });
        }

        [ConditionalFact(nameof(IsStoreReflectableWithThreads))]
        public void Record_StoresOneEntryPerCall_InRingOrder()
        {
            RunOnNewThread(() =>
            {
                var exceptions = new Exception[ContractRingSize + 2];
                for (int i = 0; i < exceptions.Length; i++)
                {
                    exceptions[i] = new Exception();
                    Record("annotation " + i, exceptions[i]);
                }

                Array ring = GetRing()!;
                Assert.Equal(ContractRingSize, ring.Length);
                Assert.Equal(exceptions.Length, GetCount());
                for (int i = 2; i < exceptions.Length; i++)
                {
                    object entry = ring.GetValue(i % ContractRingSize)!;
                    Assert.Equal("annotation " + i, GetEntryValue(entry));
                    Assert.Same(exceptions[i], GetEntryException(entry));
                }
            });
        }

        // The count is unsigned: at -1 (0xFFFFFFFF) the next entry goes to slot 7 and the count wraps to 0.
        // A signed % would compute -1 % 8 == -1 and throw on the store.
        [ConditionalFact(nameof(IsStoreReflectableWithThreads))]
        public void Record_CountAtUInt32MaxValue_WritesLastSlotAndWraps()
        {
            RunOnNewThread(() =>
            {
                SetCount(-1);
                var exception = new Exception();

                Record("annotation", exception);

                Array ring = GetRing()!;
                Assert.Same(exception, GetEntryException(ring.GetValue(ContractRingSize - 1)!));
                Assert.Equal(0, GetCount());
            });
        }

        // Starting at int.MaxValue, three entries go to slots 7, 0 and 1. The first two cannot tell signed
        // from unsigned arithmetic (int.MinValue % 8 == 0); the third can (signed: -7).
        [ConditionalFact(nameof(IsStoreReflectableWithThreads))]
        public void Record_CountCrossesInt32MaxValue_StaysInOrder()
        {
            RunOnNewThread(() =>
            {
                SetCount(int.MaxValue);
                var exceptions = new[] { new Exception(), new Exception(), new Exception() };

                foreach (Exception exception in exceptions)
                {
                    Record("annotation", exception);
                }

                Array ring = GetRing()!;
                Assert.Same(exceptions[0], GetEntryException(ring.GetValue(7)!));
                Assert.Same(exceptions[1], GetEntryException(ring.GetValue(0)!));
                Assert.Same(exceptions[2], GetEntryException(ring.GetValue(1)!));
                Assert.Equal(int.MinValue + 2, GetCount());
            });
        }

        // With no debugger attached, Annotate tests the int count before the ring reference, so that a thread that
        // never recorded never touches the GC thread-static, whose first access can allocate the thread's storage.
        [ConditionalFact(nameof(CanMeasureDetachedAllocation))]
        public void Annotate_NoDebugger_FreshThread_DoesNotAllocate()
        {
            var exception = new Exception();
            const string Annotation = "annotation";

            // Warm up on another thread, so that compiling the methods and initializing the types is not measured.
            RunOnNewThread(() => Debugger.Annotate(Annotation, exception));

            long first = -1;
            long second = -1;
            RunOnNewThread(() =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Debugger.Annotate(Annotation, exception);
                long afterFirst = GC.GetAllocatedBytesForCurrentThread();
                Debugger.Annotate(Annotation, exception);
                long afterSecond = GC.GetAllocatedBytesForCurrentThread();
                first = afterFirst - before;
                second = afterSecond - afterFirst;
            });

            Assert.Equal(0, first);
            Assert.Equal(0, second);
        }
    }
}
