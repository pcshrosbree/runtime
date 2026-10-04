// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics
{
    // DEBUGGER CONTRACT (version 1): see docs/design/features/debugger-annotations.md.
    //
    // Debuggers read the fields of this type by name, at a stop, without running any code in the
    // process. Do not rename them, change their types, add a static field, or give this type or its
    // nested Entry a static constructor or any static field initializer.
    //
    // Why no static constructor: a debugger that reads a static directly sees its storage as it is, and
    // for a type with a .cctor that can be the state from before, or during, the initializer. With no
    // .cctor there is nothing to run, the value before and after initialization is the same all-zero
    // state, and a direct read is exact. A debugger checks for that in metadata alone and ignores a
    // store whose type has a .cctor, so any initialized static silently turns this store off for every
    // debugger. Literal constants and uninitialized [ThreadStatic] fields emit no .cctor.
    //
    // Why one immutable Entry per call rather than two parallel arrays: a thread can be suspended between
    // any two stores. With a value array and an exception array, a debugger could see one call's value
    // beside another call's exception. An Entry is fully constructed before it is published with a single
    // reference store, which is atomic, so every slot holds either the old entry or the new one.
    //
    // Read order, for debuggers: with c = (uint)t_annotationCount and L the ring's length (taken from the
    // array, never assumed to be RingSize), the k-th newest entry is at (c - 1 - k) mod 2^32 mod L, for
    // k = 0 .. L-1. A debugger visits all L slots in that order and skips null ones. It never uses the
    // count to decide which slots hold entries: the count wraps, so a count of 0 does not mean an empty
    // ring, and a thread suspended between the slot store and the count store leaves them out of step.
    //
    // Lifetime: nothing is recorded while no debugger is attached, so a process that is never attached
    // never allocates a ring. After a detach, a thread keeps its entries, and through them their values
    // and exceptions, until its next Debugger.Annotate call observes the detach and clears them, or until
    // the thread exits.
    internal static class DebuggerAnnotationStore
    {
        // A power of two, so that count % RingSize stays in order across the 2^32 wrap of the count.
        // A literal constant, not a static readonly field, because an initialized static emits a .cctor.
        // Debuggers take the length from the array, so a later version may change this value.
        private const int RingSize = 8;

        // The calling thread's ring of its most recent entries, or null when the thread has recorded
        // nothing, or its ring was cleared after a detach. No initializer, deliberately (see above).
        [ThreadStatic]
        private static Entry?[]? t_annotations;

        // The number of entries ever recorded on this thread, modulo 2^32. Declared as int (the contract's
        // field signature is ELEMENT_TYPE_I4) but always interpreted as an unsigned 32-bit count.
        [ThreadStatic]
        private static int t_annotationCount;

        // Appends one entry to the calling thread's ring, allocating the ring on the thread's first call.
        // Debugger.Annotate is the checked entry point; this method does no gating and no argument checks.
        // The value is typed object, matching Entry.Value, so that a later producer of non-string values
        // needs no contract change.
        internal static void Record(object value, Exception? exception)
        {
            Entry?[] ring = t_annotations ??= new Entry?[RingSize];

            // The count wraps by design, also in a build with overflow checking. The index is computed
            // unsigned: a signed % would give a negative index once the count passes 2^31. The entry is
            // constructed before the slot store; the count is advanced after it. A debugger tolerates a
            // thread stopped between the two (or the two reordered), because it visits every slot and
            // uses the count only to order them.
            unchecked
            {
                uint count = (uint)t_annotationCount;
                ring[count % RingSize] = new Entry(value, exception);
                t_annotationCount = (int)(count + 1);
            }
        }

        // Drops the calling thread's ring, if it has one, releasing every entry it roots. Called by
        // Debugger.Annotate when no debugger is attached.
        //
        // The int count is tested first, and that order matters: this runs on every call made while no
        // debugger is attached. A thread that never recorded has a count of 0, so the test short-circuits
        // and never touches t_annotations, a GC thread-static whose first access on a thread can allocate
        // that thread's storage for GC thread-statics. Testing the reference first would put that
        // allocation on the first detached call of every thread.
        //
        // A ring whose count has wrapped to exactly 0 is not cleared here: the count does not advance while
        // detached, so the entries stay until a call made while a debugger is attached advances the count,
        // or until the thread exits. Reaching that state needs 2^32 recorded entries on one thread, and it
        // costs only retention, never a wrong attribution, because debuggers match entries by identity.
        internal static void ClearIfPresent()
        {
            if (t_annotationCount != 0 && t_annotations is not null)
            {
                t_annotations = null;
            }
        }

        // One recorded annotation and the exception it describes, published into a ring slot as a single
        // immutable object. Debuggers find this type as the nested class Entry of the holder, in the same
        // module, and read its fields by name; the field names are part of the contract, which is why they
        // do not follow the usual naming convention for internal fields.
        internal sealed class Entry
        {
            // The annotation, never null. A debugger shows a string as is and any other object by its type
            // name only, because showing more would mean running code in the process.
            internal readonly object Value;

            // The exception the annotation describes, or null for an entry that is not tied to an
            // exception. A debugger attributes the entry to an exception stop only when this is the stop's
            // current exception, compared by object identity.
            internal readonly Exception? Exception;

            internal Entry(object value, Exception? exception)
            {
                Value = value;
                Exception = exception;
            }
        }
    }
}
