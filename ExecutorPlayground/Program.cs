// See https://aka.ms/new-console-template for more information
using Autofac;
using System.Collections.Concurrent;

Console.WriteLine("Hello, World!");

var containerBuilder = new ContainerBuilder();
containerBuilder.RegisterType<ParseHandler>().InstancePerLifetimeScope();

var container = containerBuilder.Build();

var lifetimeScope = container.Resolve<ILifetimeScope>();

var input = Enumerable.Range(1, 1).Select(e => e.ToString());

var executor = new ScopedExecutor<string, int, ParseHandler>(lifetimeScope, (x, y) => x.ParseAsync(y))
    .Delayed(TimeSpan.FromSeconds(1))
    .Sequential()
    .Parallel();

var result = await executor
    .RunAsync(input.Chunk(1))
    .CombineAsync();

var dostuff = "hello";

public class ParseHandler
{
    public async Task<int> ParseAsync(string input)
    {
        Console.WriteLine($"[Thread #{Environment.CurrentManagedThreadId}, {DateTime.Now:hh:mm:ss.fff}] Parsing '{input}'.");
        await Task.Delay(1000); // Imitate work.
        var result = int.Parse(input);
        Console.WriteLine($"[Thread #{Environment.CurrentManagedThreadId}, {DateTime.Now:hh:mm:ss.fff}] Parsed {result}.");
        return result;
    }
}

public interface IExecutor<in TInput, TOutput>
{
    Task<TOutput> RunAsync(TInput input);
}

public class FuncExecutor<TInput, TOutput> : IExecutor<TInput, TOutput>
{
    private readonly Func<TInput, Task<TOutput>> _run;

    public FuncExecutor(Func<TInput, Task<TOutput>> run) =>
        _run = run;

    public Task<TOutput> RunAsync(TInput input) =>
        _run(input);
}

public class ScopedExecutor<TInput, TOutput> : ScopedExecutor<TInput, TOutput, IExecutor<TInput, TOutput>>
{
    public ScopedExecutor(ILifetimeScope scope)
        : base(scope, (x, input) => x.RunAsync(input))
    {
    }
}

public class ScopedExecutor<TInput, TOutput, TExecutor> : IExecutor<TInput, TOutput>
    where TExecutor : notnull
{
    private readonly ILifetimeScope _scope;
    private readonly Func<TExecutor, TInput, Task<TOutput>> _run;

    public ScopedExecutor(ILifetimeScope scope, Func<TExecutor, TInput, Task<TOutput>> run)
    {
        _scope = scope;
        _run = run;
    }

    public async Task<TOutput> RunAsync(TInput input)
    {
        await using var scope = _scope.BeginLifetimeScope();

        return await _run(scope.Resolve<TExecutor>(), input);
    }
}

public class DelayedExecutor<TInput, TOutput> : IExecutor<TInput, TOutput>
{
    private readonly IExecutor<TInput, TOutput> _inner;
    private readonly TimeSpan _delay;

    private bool _first = false;

    public DelayedExecutor(IExecutor<TInput, TOutput> inner, TimeSpan delay)
    {
        _inner = inner;
        _delay = delay;
    }

    public async Task<TOutput> RunAsync(TInput input)
    {
        if (!_first)
        {
            await Task.Delay(_delay);
        }

        _first = false;

        return await _inner.RunAsync(input);
    }
}

public class SequentialExecutor<TInput, TOutput> : IExecutor<IEnumerable<TInput>, IEnumerable<TOutput>>
{
    private readonly IExecutor<TInput, TOutput> _inner;

    public SequentialExecutor(IExecutor<TInput, TOutput> inner) =>
        _inner = inner;

    public async Task<IEnumerable<TOutput>> RunAsync(IEnumerable<TInput> input)
    {
        var output = new List<TOutput>();

        foreach (var item in input)
        {
            output.Add(await _inner.RunAsync(item));
        }

        return output;
    }
}

public class ParallelExecutor<TInput, TOutput> : IExecutor<IEnumerable<TInput>, IEnumerable<TOutput>>
{
    private readonly IExecutor<TInput, TOutput> _inner;

    public ParallelExecutor(IExecutor<TInput, TOutput> inner) =>
        _inner = inner;

    public async Task<IEnumerable<TOutput>> RunAsync(IEnumerable<TInput> input)
    {
        var bag = new ConcurrentBag<TOutput>();

        await Parallel.ForEachAsync(
            input, async (x, _) =>
            {
                bag.Add(await _inner.RunAsync(x));
            });

        return bag;
    }
}

//public class CombineExecutor<TInput, TOutput> : IExecutor<TInput, IEnumerable<TOutput>>
//    where TOutput : IEnumerable<IEnumerable<TOutput>>
//{
//    private readonly IExecutor<TInput, TOutput> _inner;

//    public CombineExecutor(IExecutor<TInput, TOutput> inner) =>
//        _inner = inner;

//    public async Task<IEnumerable<TOutput>> RunAsync(IEnumerable<TInput> input)
//    {
//        var results = await _inner.RunAsync

//        var result = await _inner.RunAsync(input);
//    }
//}

public static class ExecutorExtensions
{
    public static IExecutor<IEnumerable<TInput>, IEnumerable<TOutput>> Sequential<TInput, TOutput>(
        this IExecutor<TInput, TOutput> self) => new SequentialExecutor<TInput, TOutput>(self);

    public static IExecutor<IEnumerable<TInput>, IEnumerable<TOutput>> Parallel<TInput, TOutput>(
        this IExecutor<TInput, TOutput> self) => new ParallelExecutor<TInput, TOutput>(self);

    public static IExecutor<TInput, TOutput> Delayed<TInput, TOutput>(
        this IExecutor<TInput, TOutput> self, TimeSpan span) => new DelayedExecutor<TInput, TOutput>(self, span);

    //public static async Task<IEnumerable<TOutput>> RunChunkedAsync<TInput, TOutput>(
    //    this ParallelExecutor<TInput, TOutput> self, IEnumerable<TInput> input, int chunks) =>
    //        await self.RunAsync(input.Chunk(size: chunks));
}

public static class Extensions
{
    public static async Task<IEnumerable<T>> CombineAsync<T>(this Task<IEnumerable<IEnumerable<T>>> input)
    {
        var result = await input;
        return result.SelectMany(e => e);
    }
}