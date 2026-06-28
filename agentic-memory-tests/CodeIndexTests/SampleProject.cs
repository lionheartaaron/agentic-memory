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

        // Declaration-shape permutations for "View source" span correctness. Every block body carries
        // a unique VSMARK_* token so a test can prove the slice captured the WHOLE body.
        ["backend/ViewSource.cs"] = """
            using System;

            namespace Backend.ViewSrc;

            public class VsHost
            {
                public int VsExpr() => 42;

                public int VsBlock()
                {
                    var x = 1; // VSMARK_BLOCK
                    return x;
                }

                public int VsMultiSig(
                    int a,
                    int b)
                {
                    return a + b; // VSMARK_MULTISIG
                }

                [Obsolete("x")]
                public int VsAttr()
                {
                    return 0; // VSMARK_ATTR
                }

                public T VsGeneric<T>(T input)
                    where T : class
                {
                    return input; // VSMARK_GENERIC
                }

                public int VsAuto { get; set; }

                public int VsProp
                {
                    get { return _backing; } // VSMARK_PROP
                    set { _backing = value; }
                }
                private int _backing;

                public int VsExprProp => _backing;

                public const int VsConst = 7;

                public event Action? VsEvent;

                public VsHost()
                {
                    VsAuto = 1; // VSMARK_CTOR
                }
            }

            public interface IVsShape
            {
                int VsArea();
            }

            public record VsRecord(int VsFirst, string VsSecond);

            public enum VsEnum
            {
                VsA = 1,
                VsB = 2,
            }

            public struct VsStruct
            {
                public int VsX; // VSMARK_STRUCT
            }
            """,

        // Less-common C# declaration forms — to audit/guarantee extraction completeness.
        ["backend/Exotic.cs"] = """
            using System;

            namespace Backend.Exotic;

            public class ExoticHost
            {
                public int this[int i] => i;
                public static ExoticHost operator +(ExoticHost a, ExoticHost b) => a;
                public static implicit operator int(ExoticHost h) => 0;
                public int ExA, ExB;
                public delegate int ExNestedDelegate(int x);
            }

            public delegate void ExTopDelegate(string s);

            public class ExoticService(int seed)
            {
                public int Seed => seed;
            }

            public class ExoticFinalizer
            {
                ~ExoticFinalizer() { }
            }
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
            /** A user in the system. */
            export interface User {
              id: string;
              name: string;
            }

            export type Identifier = string;

            export enum Role {
              Admin = 1,
              Member = 2,
            }

            export class Repository<T> {
              private items: T[] = [];
              static empty = 0;

              /** Adds an item to the repository. */
              add(item: T): void {
                this.items.push(item);
              }

              all(): T[] {
                return this.items;
              }

              get count(): number {
                return this.items.length;
              }

              /** @deprecated use all() instead */
              legacy(): T[] {
                return this.items;
              }
            }

            export function makeUser(id: string, name: string): User {
              return { id, name };
            }

            export async function loadUser(id: string): Promise<User> {
              const res = await fetch("/api/users");
              return res.json();
            }

            export function identity<T>(value: T): T {
              return value;
            }
            """,

        ["web/src/consumer.ts"] = """
            import { Repository, makeUser } from "./models";

            export function buildRepo(): Repository<unknown> {
              const repo = new Repository();
              repo.add(makeUser("1", "Ada"));
              return repo;
            }
            """,

        // TS declaration-shape permutations for "View source" span correctness (VSMARK_* in bodies).
        ["web/src/viewsource.ts"] = """
            export const vsArrow = (a: number): number => a + 1;

            export const vsArrowBlock = (a: number): number => {
              const x = a; // VSMARK_ARROWBLOCK
              return x;
            };

            export function vsFunc(a: number): number {
              const x = a; // VSMARK_FUNC
              return x;
            }

            export function vsMultiSig(
              a: number,
              b: number
            ): number {
              return a + b; // VSMARK_MULTISIG
            }

            export async function vsAsync(): Promise<number> {
              return 1; // VSMARK_ASYNC
            }

            export interface VsShape {
              area: number;
              name: string;
            }

            export type VsUnion =
              | "a"
              | "b";

            export enum VsEnum2 {
              A = 1,
              B = 2,
            }

            export class VsClass {
              private x = 0;

              vsMethod(): number {
                return this.x; // VSMARK_METHOD
              }

              get vsGetter(): number {
                return this.x; // VSMARK_GETTER
              }
            }

            export default function VsDefault(): number {
              return 7; // VSMARK_DEFAULT
            }
            """,

        // Default-exported component + default-import + JSX usage — the React orphan-false-positive repro.
        ["web/src/Card.tsx"] = """
            export default function Card(): string {
              return "card";
            }
            """,

        ["web/src/CardConsumer.tsx"] = """
            import { Routes } from "react-router-dom";
            import Card from "./Card";

            export function CardView() {
              return (
                <Routes>
                  <Card />
                </Routes>
              );
            }
            """,

        // Less-common TS declaration forms — extraction-completeness audit.
        ["web/src/exotic.ts"] = """
            export namespace ExNs {
              export function nsFunc(): number {
                return 1;
              }
            }

            export const enum ExConstEnum {
              A,
              B,
            }

            export abstract class ExAbstract {
              abstract doThing(): void;
            }

            export const { exDestructuredA, exDestructuredB } = { exDestructuredA: 1, exDestructuredB: 2 };

            export default function () {
              return 42;
            }
            """,

        ["web/src/hooks.ts"] = """
            import { useMutation, useQueryClient } from "@tanstack/react-query";
            import { useNavigate } from "react-router-dom";

            export function useCreateOrder() {
              const queryClient = useQueryClient();
              const navigate = useNavigate();
              return useMutation({
                mutationFn: (name: string) => fetch("/api/orders", { method: "POST", body: name }),
                onSuccess: () => {
                  queryClient.invalidateQueries({ queryKey: ["orders"] });
                  navigate("/orders");
                },
              });
            }
            """,

        ["web/src/OrdersPage.tsx"] = """
            import { Link } from "react-router-dom";

            export function OrdersPage(): string {
              return "orders";
            }

            export function NewOrderLink() {
              return <Link to="/orders/new">New order</Link>;
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
