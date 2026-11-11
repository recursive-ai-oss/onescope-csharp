using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// A module is built against an earlier SDK and loaded by a newer one. Compiling every method of the bundled modules forces the
/// runtime to bind each SDK member they call, so a removed or changed member shows up here as a missing-member error instead of
/// when someone opens a file that happens to reach it.
/// </summary>
public sealed class BundledModuleBindingTests
{
    [Theory]
    [InlineData(typeof(RecursiveAI.OnePhysics.Scope.OnePhysicsModule))]
    [InlineData(typeof(RecursiveAI.OnePath.Scope.OnePathModule))]
    public void EveryMethodOfABundledModuleBindsAgainstTheCurrentSdk(Type marker)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        int prepared = 0;
        foreach (Type type in marker.Assembly.GetTypes())
        {
            if (type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (MethodBase method in type.GetConstructors(all).Cast<MethodBase>().Concat(type.GetMethods(all)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null)
                {
                    continue;
                }

                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepared++;
            }
        }

        Assert.True(prepared > 50, $"Only {prepared} methods were compiled.");
    }
}
