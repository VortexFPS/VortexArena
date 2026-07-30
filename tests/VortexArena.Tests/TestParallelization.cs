// Disable xUnit's cross-collection parallelism for this assembly.
//
// The ported gameplay/physics code reaches the engine through process-global ambient state — most notably
// `XonoticGodot.Common.Services.Api.Services` (the trace/cvar/clock facade) and the static `MutatorHooks` /
// `GameRegistries` tables. Many tests install their own facade into `Api.Services` for the duration of a
// test; when xUnit runs test collections in parallel, two such tests race on that single static and one
// clobbers the other mid-run (e.g. a movement-parity sim reading another test's `sv_gravity`). This is the
// pre-existing "global-state race" the suite was meant to be confirmed against sequentially.
//
// Running serially makes every test deterministic regardless of order. The suite is small (well under a
// second), so the lost parallelism is irrelevant, and there is no real concurrency in the code under test.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
