namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Writes a self-contained, multi-sub-project workspace to disk for the code-index integration
/// tests: a C# backend sub-project (<c>backend/</c>) and a TypeScript/React sub-project (<c>web/</c>).
///
/// The C# seed is crafted so the Roslyn compilation resolves every reference it needs internally —
/// framework types the domain extractors match on (EF <c>DbSet&lt;T&gt;</c>, MediatR
/// <c>IRequest</c>/<c>IRequestHandler</c>) are declared in-tree under their real namespaces, because
/// those packages are not on the test host's reference set. Types that ARE on the shared framework
/// (Task, IDisposable, Process, DataAnnotations) are used directly and must NOT be re-declared, or
/// resolution becomes ambiguous.
/// </summary>
internal static class SampleProject
{
    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>
    {
        // ── C# backend sub-project ───────────────────────────────────────────────
        ["backend/Backend.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <OutputType>Library</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MediatR" Version="12.0.0" />
              </ItemGroup>
            </Project>
            """,

        // Framework stand-ins so the domain extractors resolve the types they match by name+namespace.
        // (MediatR / EF Core are not on the test host's reference set; the BCL types we use are.)
        ["backend/Framework.cs"] = """
            namespace Microsoft.EntityFrameworkCore
            {
                public class DbSet<T> { }
            }

            namespace MediatR
            {
                public interface IRequest<TResponse> { }
                public interface IRequestHandler<TRequest, TResponse> { }
            }
            """,

        ["backend/Domain.cs"] = """
            using System;
            using System.Threading.Tasks;
            using System.ComponentModel.DataAnnotations;
            using Microsoft.EntityFrameworkCore;
            using MediatR;

            namespace Backend;

            /// <summary>Abstraction over the system clock.</summary>
            public interface IClock
            {
                DateTime UtcNow { get; }
            }

            public sealed class SystemClock : IClock
            {
                public DateTime UtcNow => DateTime.UtcNow;
            }

            /// <summary>Performs arithmetic operations.</summary>
            public class Calculator
            {
                /// <summary>Adds two integers.</summary>
                /// <param name="a">The first addend.</param>
                public int Add(int a, int b) => a + b;

                // Overload — exercises per-symbol identity in the reference index.
                public int Add(int a, int b, int c) => a + b + c;

                [Obsolete("Use Add instead.")]
                public int Sum(int a, int b) => a + b;

                // Never referenced anywhere → orphan.
                public int Unused() => 0;

                // Private — must not produce a symbol-reference record.
                private int Secret() => 42;
            }

            // Explicit public constructor reached only through `new` — the regression target:
            // it must NOT be reported as an orphan.
            public class Widget
            {
                public Widget(int size) { Size = size; }
                public int Size { get; }
            }

            // Explicit constructor on a type nothing references → genuinely dead, still flagged.
            public class Lonely
            {
                public Lonely() { }
            }

            [Flags]
            public enum Permission
            {
                None  = 0,
                Read  = 1,
                Write = 2,
            }

            public enum Status
            {
                Active   = 1,
                Inactive = 2,
                Pending  = 3,
            }

            // DataAnnotations validation on explicit properties (resolved via the shared framework).
            public class CreateUserRequest
            {
                [Required]
                public string Name { get; set; } = "";

                [Range(1, 120)]
                public int Age { get; set; }
            }

            // Constructor injection of an abstraction → di-injection fact.
            public class OrderService
            {
                private readonly IClock _clock;
                public OrderService(IClock clock) { _clock = clock; }

                public async Task<int> CountAsync()
                {
                    await Task.Delay(1);
                    return _clock.UtcNow.Day;
                }
            }

            public class Order
            {
                public int Id { get; set; }
            }

            // EF entity surface → ef-entity fact (DbSet<T> stand-in declared in Framework.cs).
            public class AppDbContext
            {
                public DbSet<Order> Orders { get; set; } = new();
            }

            // MediatR request/handler pair → mediatr-message + mediatr-handler facts.
            public class GetOrder : IRequest<Order> { }
            public class GetOrderHandler : IRequestHandler<GetOrder, Order> { }

            // Type relations → type-relation facts (extends / implements).
            public class Animal { }
            public interface IBark { }
            public class Dog : Animal, IBark { }

            // Resource/concurrency contract flags.
            public sealed class ResourceHolder : IDisposable
            {
                public void Dispose() { }
            }

            // Generics — type-parameter capture.
            public class Box<T> where T : class
            {
                public T? Value { get; set; }
            }
            """,

        ["backend/Endpoints.cs"] = """
            namespace Backend;

            // Minimal-API endpoint mapping. `app` is deliberately untyped so MapGet/MapPost/MapDelete
            // do not resolve — exercising the syntactic "/route" fallback the real app relies on
            // (the ASP.NET shared-framework assemblies are usually absent from a project's bin closure).
            public static class Endpoints
            {
                public static void Map(object app)
                {
                    app.MapGet("/api/orders", () => "list");
                    app.MapPost("/api/orders", () => "create");
                    app.MapDelete("/api/orders/{id}", () => "delete");
                }
            }
            """,

        ["backend/Infrastructure.cs"] = """
            using System.Diagnostics;

            namespace Backend;

            public static class Infrastructure
            {
                // config-key fact via the GetValue convention (config is untyped → syntactic match).
                public static string ReadSetting(object config)
                    => config.GetValue("My:Setting") ?? "";

                // security-sink fact — Process.Start resolves through the shared framework.
                public static void Launch()
                {
                    Process.Start("cmd");
                }
            }
            """,

        ["backend/Consumer.cs"] = """
            namespace Backend;

            // Cross-file consumer — drives fan-in / references / orphan resolution.
            public class Consumer
            {
                public int Run()
                {
                    var calc = new Calculator();
                    var two  = calc.Add(1, 2);
                    var three = calc.Add(1, 2, 3);
                    var w = new Widget(5);
                    var svc = new OrderService(new SystemClock());
                    return two + three + w.Size;
                }
            }
            """,

        // Test file (by xunit import + *Tests.cs name) in the same compilation, so its reference to
        // Calculator resolves → exercises test-linkage rollups (TestedByFileIds / IsTestFile).
        ["backend/CalculatorTests.cs"] = """
            using Xunit;

            namespace Backend.Tests;

            public class CalculatorTests
            {
                [Fact]
                public void AddWorks()
                {
                    var c = new Calculator();
                    Assert.Equal(3, c.Add(1, 2));
                }
            }
            """,

        // Exhaustive surface for the structured-symbol + reference-role extractors.
        ["backend/Variety.cs"] = """
            using System;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;
            using System.Threading.Tasks;

            namespace Backend;

            // All six accessibility forms — the GetAccessibility regression target.
            public class AccessSample
            {
                public int Pub;
                internal int Intl;
                protected int Prot;
                private int Priv;
                protected internal int ProtIntl;
                private protected int PrivProt;
            }

            public abstract class ModBase
            {
                public const int ConstField = 5;
                public readonly int ReadonlyField = 1;
                public static int StaticM() => 0;
                public virtual int VirtualM() => 1;
                public abstract int AbstractM();
            }

            public sealed class ModDerived : ModBase
            {
                public override int AbstractM() => 2;
                public sealed override int VirtualM() => 3;
            }

            public interface IShape
            {
                double Area { get; }
            }

            public struct PointStruct
            {
                public int X;
                public int Y;
            }

            public record PersonRecord([property: Required] string First, string Last);

            public class PropShapes
            {
                public int Auto { get; set; }
                public int ReadOnlyProp { get; }
                public int InitProp { get; init; }
            }

            public class Notifier
            {
                public event Action? Changed;
            }

            public static class ParamShapes
            {
                public static int WithDefaults(int required, int optional = 7, string label = "x") => required;
                public static void WithParams(params int[] nums) { }
                public static bool TryThing(int input, out int result) { result = input; return true; }
                public static void ByRef(ref int x) { x++; }
            }

            public static class ReturnShapes
            {
                public static ValueTask<string> ValueTaskM() => default;
                public static async IAsyncEnumerable<int> StreamM() { await Task.CompletedTask; yield return 1; }
                public static Task PlainTask() => Task.CompletedTask;
            }

            public class ConcurrencySample
            {
                private readonly object _lock = new();
                private int _count;
                public void Locked() { lock (_lock) { _count++; } }
                public int Blocking() => Task.FromResult(1).Result;
            }

            // Reference-role producer — consumed cross-file by RoleConsumer.cs so its usage sites
            // (with roles + caller attribution) land in the UsedBy graph.
            public class RoleProducer
            {
                public int Field;
                public int Prop { get; set; }
                public void Method() { }
            }

            public class BaseHook { public virtual void Hook() { } }
            public class DerivedHook : BaseHook { public override void Hook() { } }
            """,

        ["backend/RoleConsumer.cs"] = """
            namespace Backend;

            // Cross-file consumer that exercises every reference role + caller attribution.
            public class RoleConsumer
            {
                public int Caller(RoleProducer p)
                {
                    var read = p.Field;            // read
                    p.Field = 5;                   // write
                    p.Prop = 3;                    // write
                    var pr = p.Prop;               // read
                    p.Method();                    // call
                    RoleProducer alias = p;        // typeref
                    var made = new RoleProducer(); // new
                    return read + pr;
                }
            }
            """,

        // ── TypeScript / React web sub-project ───────────────────────────────────
        ["web/package.json"] = """
            {
              "name": "web",
              "version": "1.0.0",
              "dependencies": {
                "react": "^18.0.0",
                "@tanstack/react-query": "^5.0.0"
              },
              "devDependencies": {
                "typescript": "^5.5.4",
                "vite": "^5.0.0"
              },
              "scripts": {
                "build": "vite build",
                "dev": "vite"
              }
            }
            """,

        ["web/tsconfig.json"] = """
            {
              "compilerOptions": {
                "target": "ES2020",
                "module": "ESNext",
                "jsx": "react-jsx",
                "strict": true,
                "moduleResolution": "Bundler"
              },
              "include": ["src"]
            }
            """,

        ["web/src/utils.ts"] = """
            export function formatName(first: string, last: string): string {
              return `${first} ${last}`;
            }

            export const VERSION = "1.0.0";
            """,

        ["web/src/api.ts"] = """
            export async function fetchUser(id: string): Promise<unknown> {
              const res = await fetch(`/api/users/${id}`);
              return res.json();
            }
            """,

        ["web/src/useOrders.ts"] = """
            import { useQuery } from "@tanstack/react-query";

            export function useOrders() {
              return useQuery({
                queryKey: ["orders"],
                queryFn: () => fetch("/api/orders").then((r) => r.json()),
              });
            }
            """,

        ["web/src/App.tsx"] = """
            import { formatName } from "./utils";

            export function App(): string {
              return formatName("Ada", "Lovelace");
            }
            """,

        ["web/src/models.ts"] = """
            export interface User {
              id: string;
              name: string;
            }

            export type Identifier = string;

            export class Repository<T> {
              private items: T[] = [];
              add(item: T): void {
                this.items.push(item);
              }
              all(): T[] {
                return this.items;
              }
            }

            export function makeUser(id: string, name: string): User {
              return { id, name };
            }
            """,
    };

    /// <summary>Writes the seed tree under <paramref name="root"/> and returns the same path.</summary>
    public static string Write(string root)
    {
        foreach (var (relative, content) in Files)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }
}
