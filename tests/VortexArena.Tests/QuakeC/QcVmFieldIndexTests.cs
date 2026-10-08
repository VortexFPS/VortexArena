using System;
using System.Collections.Generic;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.QuakeC;

/// <summary>
/// What the VM keeps beside the entity fields so that engine loops need not read them
/// (<see cref="QcVm.WatchFields"/>, <see cref="QcVm.MirrorField"/>), and the string cache behind the
/// string builtins. None of it may be visible to a program; each must stay true through every way a
/// field can change: a STOREP, a cleared entity, a copied one, an engine write, a grown entity array.
/// </summary>
public class QcVmFieldIndexTests
{
    // A program with fields health (float), think (function), origin (vector), solid (float), and
    // one function "set" that does: ent.FIELD = VALUE for whatever the test put in the globals.
    private sealed class Rig
    {
        public readonly QcVm Vm;
        public readonly int Health, Think, Origin, Solid;
        private readonly int _ent, _field, _value, _vector, _set, _setVector;

        public Rig()
        {
            ProgsBuilder b = new();
            int health = b.Field("health"), think = b.Field("think", QcType.Function), origin = b.Field("origin", QcType.Vector), solid = b.Field("solid");
            _ent = b.Int(0, "ent", QcType.Entity);
            _field = b.Int(0, "fld", QcType.Field);
            _value = b.Int(0, "value");
            _vector = b.Vector(0, 0, 0, "vec");
            int pointer = b.Int(0);
            b.Function("set");
            b.Emit(QcOp.Address, _ent, _field, pointer);
            b.Emit(QcOp.StorepF, _value, pointer);
            b.Emit(QcOp.Return);
            b.Function("setvector");
            b.Emit(QcOp.Address, _ent, _field, pointer);
            b.Emit(QcOp.StorepV, _vector, pointer);
            b.Emit(QcOp.Return);
            Vm = b.BuildVm();
            Health = Vm.FindField("health")!.Offset;
            Think = Vm.FindField("think")!.Offset;
            Origin = Vm.FindField("origin")!.Offset;
            Solid = Vm.FindField("solid")!.Offset;
            _set = Vm.FindFunction("set");
            _setVector = Vm.FindFunction("setvector");
            _ = health; _ = think; _ = origin; _ = solid;
        }

        public void Set(int edict, int fieldOffset, int rawValue)
        {
            Vm.GlobalInt(_ent) = edict;
            Vm.GlobalInt(_field) = fieldOffset;
            Vm.GlobalInt(_value) = rawValue;
            Vm.Execute(_set);
        }

        public void SetVector(int edict, int fieldOffset, float x, float y, float z)
        {
            Vm.GlobalInt(_ent) = edict;
            Vm.GlobalInt(_field) = fieldOffset;
            Vm.GlobalVector(_vector) = new QcVector(x, y, z);
            Vm.Execute(_setVector);
        }
    }

    private static List<int> Watched(QcVm vm)
    {
        List<int> found = new();
        for (int e = vm.NextWatched(0); e >= 0; e = vm.NextWatched(e + 1)) found.Add(e);
        return found;
    }

    [Fact]
    public void Watch_marks_an_entity_when_the_program_stores_a_non_zero_value_in_a_watched_field()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict(), b = r.Vm.AllocEdict(), c = r.Vm.AllocEdict();
        r.Vm.WatchFields(stackalloc int[] { r.Think });
        Assert.Empty(Watched(r.Vm));

        r.Set(b, r.Think, 7);
        Assert.Equal(new[] { b }, Watched(r.Vm));
        Assert.True(r.Vm.IsWatched(b));
        Assert.False(r.Vm.IsWatched(a));

        // Another field of the same entity, and a zero in the watched one, mark nothing.
        r.Set(a, r.Health, 100);
        r.Set(c, r.Think, 0);
        Assert.Equal(new[] { b }, Watched(r.Vm));
    }

    [Fact]
    public void Watch_finds_what_was_already_set_when_watching_began()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict(), b = r.Vm.AllocEdict();
        r.Set(b, r.Think, 3);
        r.Vm.WatchFields(stackalloc int[] { r.Think });
        Assert.Equal(new[] { b }, Watched(r.Vm));
        _ = a;
    }

    [Fact]
    public void Watch_bit_goes_when_the_entity_is_cleared_and_only_then_or_when_checked_empty()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict();
        r.Vm.WatchFields(stackalloc int[] { r.Think });
        r.Set(a, r.Think, 9);

        // Still non-zero: the VM refuses to drop it, whatever the caller believes.
        Assert.False(r.Vm.ClearWatched(a));
        Assert.True(r.Vm.IsWatched(a));

        // The program zeroes the field: the bit stays (it is conservative) until a reader drops it.
        r.Set(a, r.Think, 0);
        Assert.True(r.Vm.IsWatched(a));
        Assert.True(r.Vm.ClearWatched(a));
        Assert.False(r.Vm.IsWatched(a));

        // Set again, then freed: cleared with the entity.
        r.Set(a, r.Think, 9);
        Assert.True(r.Vm.IsWatched(a));
        r.Vm.FreeEdict(a, 0);
        Assert.False(r.Vm.IsWatched(a));
    }

    [Fact]
    public void Watch_follows_a_copied_entity_and_an_engine_write()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict(), b = r.Vm.AllocEdict(), c = r.Vm.AllocEdict();
        r.Vm.WatchFields(stackalloc int[] { r.Think });
        r.Set(a, r.Think, 4);
        r.Vm.CopyEdict(a, b);
        Assert.Equal(new[] { a, b }, Watched(r.Vm));
        Assert.Equal(4, r.Vm.FieldInt(b, r.Think));

        // An engine write through a reference needs no report.
        r.Vm.FieldInt(c, r.Think) = 5;
        Assert.Equal(new[] { a, b, c }, Watched(r.Vm));
    }

    [Fact]
    public void Watch_sees_a_vector_store_that_covers_the_watched_cell()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict(), b = r.Vm.AllocEdict();
        // Watch the middle cell of origin.
        r.Vm.WatchFields(stackalloc int[] { r.Origin + 1 });
        r.SetVector(a, r.Origin, 1, 0, 3);
        Assert.Empty(Watched(r.Vm));
        r.SetVector(b, r.Origin, 0, 2, 0);
        Assert.Equal(new[] { b }, Watched(r.Vm));
    }

    [Fact]
    public void Watch_survives_the_entity_array_growing_and_finds_entities_beyond_the_first_word()
    {
        Rig r = new();
        r.Vm.WatchFields(stackalloc int[] { r.Think });
        List<int> expected = new();
        for (int i = 0; i < 1500; i++)
        {
            int e = r.Vm.AllocEdict();
            if (i % 97 != 0) continue;
            r.Set(e, r.Think, i + 1);
            expected.Add(e);
        }
        Assert.Equal(expected, Watched(r.Vm));
        // From the middle, and one past the last.
        Assert.Equal(expected[3], r.Vm.NextWatched(expected[2] + 1));
        Assert.Equal(-1, r.Vm.NextWatched(expected[^1] + 1));
    }

    [Fact]
    public void Watch_loop_meets_an_entity_marked_ahead_of_it_while_it_runs()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict(), b = r.Vm.AllocEdict(), c = r.Vm.AllocEdict();
        r.Vm.WatchFields(stackalloc int[] { r.Think });
        r.Set(a, r.Think, 1);
        List<int> visited = new();
        for (int e = r.Vm.NextWatched(1); e > 0; e = r.Vm.NextWatched(e + 1))
        {
            visited.Add(e);
            if (e == a) r.Set(c, r.Think, 1); // what a think function that arms another entity does
        }
        Assert.Equal(new[] { a, c }, visited);
        _ = b;
    }

    [Fact]
    public void Mirror_is_the_field_through_every_kind_of_write()
    {
        Rig r = new();
        int a = r.Vm.AllocEdict(), b = r.Vm.AllocEdict();
        r.Set(a, r.Solid, BitConverter.SingleToInt32Bits(2f));
        int mirror = r.Vm.MirrorField(r.Solid);
        Assert.Equal(mirror, r.Vm.MirrorField(r.Solid)); // asked twice: the same one
        Assert.Equal(2f, r.Vm.MirroredFloat(mirror, a));
        Assert.Equal(0f, r.Vm.MirroredFloat(mirror, b));

        // The program stores, including a zero.
        r.Set(b, r.Solid, BitConverter.SingleToInt32Bits(4f));
        Assert.Equal(4f, r.Vm.MirroredFloat(mirror, b));
        r.Set(a, r.Solid, 0);
        Assert.Equal(0f, r.Vm.MirroredFloat(mirror, a));
        Assert.Null(r.Vm.VerifyMirrors());

        // A copy, a free, and an engine write through a reference, which needs no report.
        r.Vm.CopyEdict(b, a);
        Assert.Equal(4f, r.Vm.MirroredFloat(mirror, a));
        r.Vm.FreeEdict(b, 0);
        Assert.Equal(0f, r.Vm.MirroredFloat(mirror, b));
        r.Vm.FieldFloat(a, r.Solid) = 1f;
        Assert.Equal(1f, r.Vm.MirroredFloat(mirror, a));
        ref float held = ref r.Vm.FieldFloat(a, r.Solid);
        held = 3f;
        Assert.Equal(3f, r.Vm.MirroredFloat(mirror, a));
        Assert.Null(r.Vm.VerifyMirrors());

        // Other fields of the entity do not touch it.
        r.Set(a, r.Health, 55);
        r.SetVector(a, r.Origin, 1, 2, 3);
        Assert.Equal(3f, r.Vm.MirroredFloat(mirror, a));
        Assert.Null(r.Vm.VerifyMirrors());
    }

    [Fact]
    public void Mirror_survives_the_entity_array_growing()
    {
        Rig r = new();
        int mirror = r.Vm.MirrorField(r.Solid);
        int last = 0;
        for (int i = 0; i < 1500; i++)
        {
            last = r.Vm.AllocEdict();
            r.Set(last, r.Solid, i);
        }
        Assert.Equal(1499, r.Vm.Mirrored(mirror, last));
        Assert.Null(r.Vm.VerifyMirrors());
    }

    [Fact]
    public void Fields_cannot_be_added_once_they_are_watched_or_mirrored()
    {
        Rig watched = new();
        watched.Vm.WatchFields(stackalloc int[] { watched.Think });
        Assert.Throws<InvalidOperationException>(() => watched.Vm.EnsureField("extra", QcType.Float));
        Rig mirrored = new();
        mirrored.Vm.MirrorField(mirrored.Solid);
        Assert.Throws<InvalidOperationException>(() => mirrored.Vm.EnsureField("extra", QcType.Float));
        // A field the program already has is still found.
        Assert.Equal(mirrored.Solid, mirrored.Vm.EnsureField("solid", QcType.Float));
    }

    [Fact]
    public void Touch_recording_and_prefetch_change_nothing_and_survive_growth()
    {
        Rig r = new();
        r.Vm.RecordTouches = true;
        int last = 0;
        for (int i = 0; i < 1500; i++)
        {
            last = r.Vm.AllocEdict();
            r.Set(last, r.Health, i + 1);
            if (i % 200 == 0) r.Vm.PrefetchRecorded();
        }
        r.Vm.PrefetchRecorded();
        r.Vm.PrefetchRecorded(); // nothing recorded since: still fine
        Assert.Equal(1500, r.Vm.FieldInt(last, r.Health));
        r.Vm.RecordTouches = false;
        r.Vm.PrefetchRecorded();
        r.Set(last, r.Health, 7);
        Assert.Equal(7, r.Vm.FieldInt(last, r.Health));
    }

    [Fact]
    public void Cached_strings_have_the_text_asked_for_and_are_reused()
    {
        QcVm vm = new Rig().Vm;
        string first = vm.CachedString("health 100");
        Assert.Equal("health 100", first);
        Assert.Same(first, vm.CachedString("xhealth 100y".AsSpan(1, 10)));
        Assert.Equal("", vm.CachedString(ReadOnlySpan<char>.Empty));
        // A different text that lands in the same slot replaces it and is itself right.
        for (int i = 0; i < 20000; i++) Assert.Equal("n" + i, vm.CachedString("n" + i));
        Assert.Equal("health 100", vm.CachedString("health 100"));
        // Long ones are not kept, only made.
        string big = new('x', 500);
        Assert.Equal(big, vm.CachedString(big));
    }

    [Fact]
    public void Backward_and_forward_jumps_keep_their_sign_in_the_compact_instruction_stream()
    {
        // for (i = 0; i < 1000; i++) total += 2;  -- a GOTO back and an IFNOT forward, many times over.
        ProgsBuilder b = new();
        int total = b.Float(0), i = b.Float(0), one = b.Float(1), two = b.Float(2), limit = b.Float(1000), cond = b.Float(0);
        b.Function("main");
        int top = b.Emit(QcOp.LtF, i, limit, cond);
        int exit = b.Emit(QcOp.IfNot, cond);
        b.Emit(QcOp.AddF, total, two, total);
        b.Emit(QcOp.AddF, i, one, i);
        b.PatchJump(b.Emit(QcOp.Goto), top);
        b.PatchJump(exit, b.NextStatement);
        b.Emit(QcOp.Return, total);
        QcVm vm = b.BuildVm();
        vm.Execute(vm.FindFunction("main"));
        Assert.Equal(2000f, vm.ResultFloat);
    }
}
