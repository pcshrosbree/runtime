# Debugger annotations

`System.Diagnostics.Debugger.Annotate` lets code in a process leave context for the developer's next debugger stop on the calling thread. It never stops the process and never sends anything to the debugger. A debugger reads the annotations at a stop, without running code in the debuggee.

This document is the contract for **debugger authors**. The names and types below are a stable promise: they do not change from release to release without a new contract version.

## The API

```csharp
public static void Annotate(string annotation, Exception exception);
```

- The store keeps the annotation as `object` (see "Fields"), so a later overload taking another type, and a library's own copy of the store, need no contract change. A debugger therefore handles a non-string value too, as described under "Reading at an exception stop".
- A null argument throws `ArgumentNullException` on every runtime, attached or not.
- The call records nothing unless `Debugger.IsSupported` and `Debugger.IsAttached` are both true; otherwise it stores one entry in the calling thread's ring. An annotation made while no managed debugger is attached is dropped. A call made while detached may clear the thread's earlier entries (see "Lifetime").
- Annotations stay with the calling thread; they do not flow with the async context.
- The store also allows an entry with no exception, reserved for a possible later API that records context not tied to an exception; a debugger never shows such an entry at an exception stop.

## Contract version 1

### The holder

`System.Diagnostics.DebuggerAnnotationStore`, an `internal static class` in `System.Private.CoreLib`. It has **no static constructor**: no `.cctor` method in its metadata. That is what makes a direct read exact: a direct static read can otherwise see a value from before, or during, the type initializer; with no initializer there is nothing to run, and the debugger checks that from metadata.

A library MAY define its own `internal static class System.Diagnostics.DebuggerAnnotationStore` with the same members, to support runtimes that do not have this API (the pattern the compiler uses for `IsExternalInit`). A debugger treats every type of this name, in every module, as a separate store. *(Open question for review: whether to bless a library-defined type in `System.Diagnostics`, or to give library copies another name.)*

### Fields

| field | declaration |
|---|---|
| `t_annotations` | `[ThreadStatic] private static DebuggerAnnotationStore.Entry?[]? t_annotations` |
| `t_annotationCount` | `[ThreadStatic] private static int t_annotationCount` |

Nested `sealed class Entry`, instance fields only:

| field | declaration |
|---|---|
| `Value` | `readonly object Value` (never null) |
| `Exception` | `readonly System.Exception? Exception` |

A debugger locates the fields by name and checks their signatures: `SZARRAY` of the holder's nested `Entry`; `I4`; `OBJECT`; `CLASS` resolving to `System.Exception` by namespace and name. A store that does not match is ignored entirely.

### The ring

- The ring's length `L` is taken from the array. It is a power of two between 1 and 64 (8 in this version). Ignore a store whose ring has any other length.
- The producer stores a new `Entry` at `t_annotations[c % L]`, where `c` is `t_annotationCount` read as an unsigned 32-bit value, then sets the count to `c + 1`, wrapping at 2^32.
- The k-th newest entry is at `(c - 1 - k) mod 2^32 mod L`.
- **Visit all `L` slots** and skip null ones. Use the count only for ordering, never to decide which slots hold entries: after a wrap the count can be small with a full ring, and on a thread suspended inside the producer the count can lag or lead the slot by one.

### What a debugger may observe

| observation | meaning |
|---|---|
| no type of the holder's name | no store |
| a holder with a `.cctor`, generic parameters, or mismatched signatures | not a contract store: ignore it |
| `CORDBG_E_STATIC_VAR_NOT_AVAILABLE` | the thread's storage does not exist: it has recorded nothing |
| a null ring | the thread has recorded nothing, or its ring was cleared after a detach |
| `t_annotationCount` reads 0 while the ring is unavailable | normal; the `int` may live in the thread's local data block |
| any other read failure | skip this store for this thread |
| a null slot | never written |
| an object that is not the store's own `Entry` (compared by module and type token) | not a contract entry: skip it |
| an entry with a null `Value` | not a contract entry: skip it |
| an entry with a null `Exception` | not tied to an exception: never shown at an exception stop |
| an entry whose `Exception` is another object | not a match |
| an entry whose `Exception` is the current exception, by address | a match |

### The rules

A debugger MUST NOT:

1. run managed code to read or display annotations: no func-eval, including the evaluations an expression evaluator makes on its own (to initialize a type, or to format a value through `ToString()`, `[DebuggerDisplay]` or `[DebuggerTypeProxy]`);
2. read a store whose holder has a `.cctor`;
3. attribute an entry to an exception stop for any reason other than the identity match. In particular it must not count annotations since the thread's previous stop: a thread can handle exception A and then stop on an unrelated B;
4. let a failure to read a store fail or delay the stop's own report: it skips that store;
5. keep a strong reference to an entry or its value past the stop. Object addresses are compared within one stop, with no evaluation in between; a debugger that runs its own evaluations at a stop (for example to render the exception) reads the store after them, because an evaluation can invalidate values read earlier;
6. write to the debuggee, with one exception: just before it detaches, while the process is stopped, it MAY set each thread's `t_annotations` to null (`ICorDebugReferenceValue::SetValue(0)`), to release the entries.

## Reading at an exception stop

1. Get the current exception `E` of the stopped thread `T` (`ICorDebugThread::GetCurrentException`) and its address.
2. For each store (the one in `System.Private.CoreLib` first, then the others in the order the debugger first observed their modules), read `T`'s ring with `ICorDebugClass::GetStaticFieldValue(field, frame)`. The frame is used only to identify the thread; any managed frame of the thread works.
3. For each store, show the newest entry on `T` whose `Exception` is `E`.
4. **For each store with no match on `T`**, search the other managed threads' rings in that store, using any managed frame of each thread (walk the stack when the active frame is null, as it is for a thread waiting in native code). A match in one store never stops the search in another. Show the newest match of **every** thread that has one, each labelled with its thread; no order between threads is implied. `ExceptionDispatchInfo.Throw` and `await` rethrow the same exception instance, possibly on another thread, so this can find their annotations. It is best effort: the recording thread must still be alive and readable, and the entry not evicted.
5. Display a `string` value as is; anything else as its type name, optionally expandable.

On a thread suspended part-way through `Annotate`, "newest" can be approximate: a debugger may show an older annotation of the same exception. It can never show another exception's.

## Lifetime

- An entry stays reachable until its slot is overwritten, until its thread exits, or until a call to `Annotate` on that thread observes that no debugger is attached, which clears the thread's ring. That clearing is best effort: a detach followed by a re-attach with no call in between keeps the old entries, and when a thread's count has wrapped to exactly 0, no call clears the ring until the next call made while a debugger is attached.
- So while a debugger is attached, each thread keeps up to `L` entries per store, and through them their values and exceptions. A debugger that wants them released at detach clears them itself (rule 6).

## Platform notes

- **CoreCLR:** as above.
- **Mono:** the same implementation; the Mono debugger agent can read a thread-static for a given thread without running code. No Mono reader exists yet.
- **Native AOT:** there is no managed debugger, so `IsAttached` is false and nothing is recorded (the argument check still runs). With `IsSupported=false`, the default unless `DebuggerSupport=true`, the holder is trimmed.

> [!NOTE]
> This document was drafted with AI assistance (Anthropic Claude and OpenAI Codex), under my direction. I reviewed it before submitting.
