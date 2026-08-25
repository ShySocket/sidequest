using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

public static class Runner
{
    public static int Main()
    {
        int passed = 0;
        int failed = 0;
        Type[] fixtures = typeof(Runner).Assembly.GetTypes()
            .Where(type => type.GetMethods()
                .Any(method => method.GetCustomAttribute<TestAttribute>() != null))
            .OrderBy(type => type.Name)
            .ToArray();

        foreach (Type fixture in fixtures)
        {
            foreach (MethodInfo test in fixture.GetMethods()
                .Where(method => method.GetCustomAttribute<TestAttribute>() != null))
            {
                object instance = Activator.CreateInstance(fixture);
                try
                {
                    test.Invoke(instance, null);
                    passed++;
                    Console.WriteLine($"PASS {fixture.Name}.{test.Name}");
                }
                catch (TargetInvocationException wrapper)
                {
                    failed++;
                    Exception inner = wrapper.InnerException ?? wrapper;
                    Console.WriteLine($"FAIL {fixture.Name}.{test.Name}");
                    Console.WriteLine($"     {inner.GetType().Name}: {inner.Message.Replace("\n", "\n     ")}");
                    if (!(inner is AssertionException))
                    {
                        Console.WriteLine(inner.StackTrace);
                    }
                }
            }
        }

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
