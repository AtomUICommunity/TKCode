using Xunit;
using System.Text.Json;
using TKCode.Terminal.Core;
using TKCode.Terminal.Core.Interop;

namespace TKCode.Terminal.Core.Tests;

/// <summary>原生库加载、构建信息、以及手写结构体与 ghostty_type_json() 清单的布局比对（02 §4.2）。</summary>
public sealed class RuntimeTests
{
    [Fact]
    public void ReportsVersionAndBuildId()
    {
        Assert.False(string.IsNullOrEmpty(GhosttyRuntime.Version));
        var library = Directory.GetFiles(AppContext.BaseDirectory, "*ghostty-vt.*", SearchOption.AllDirectories).Single(f => !f.EndsWith(".txt") && (!f.Contains("runtimes") || f.Contains(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)));
        Assert.Matches("\\+sha256:[0-9a-f]{64}$", GhosttyRuntime.ComputeBuildId(library));
    }

    /// <summary>
    /// 生成的结构体（显式布局）与本平台库的 ghostty_type_json() 清单逐个比对大小与字段偏移。
    /// 绑定在 macOS 上生成；在其他 RID 上运行这个测试，就能发现平台之间的布局差异（02 §4.2）。
    /// </summary>
    [Fact]
    public void GeneratedStructsMatchTypeManifest()
    {
        using var doc = JsonDocument.Parse(GhosttyRuntime.TypeManifestJson());
        var types = doc.RootElement.GetProperty("types");
        var structs = typeof(GhosttyResult).Assembly.GetTypes()
            .Where(t => t.Namespace == "TKCode.Terminal.Core.Interop" && t.IsValueType && !t.IsEnum && t.Name.StartsWith("Ghostty"))
            .ToList();
        Assert.True(structs.Count > 50, $"only {structs.Count} generated structs found");
        var sizeOf = typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!;
        foreach (var type in structs)
        {
            var manifest = types.GetProperty(type.Name);
            Assert.True((int)sizeOf.MakeGenericMethod(type).Invoke(null, null)! == manifest.GetProperty("size").GetInt32(), $"{type.Name} size");
            foreach (var field in manifest.GetProperty("fields").EnumerateObject())
            {
                var member = type.GetFields().SingleOrDefault(f => string.Equals(f.Name.TrimStart('@', '_').Replace("Raw", ""),
                    field.Name.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
                Assert.True(member is not null, $"{type.Name}.{field.Name} missing");
                var offset = member!.GetCustomAttributes(typeof(System.Runtime.InteropServices.FieldOffsetAttribute), false)
                    .Cast<System.Runtime.InteropServices.FieldOffsetAttribute>().Single().Value;
                Assert.True(offset == field.Value.GetProperty("offset").GetInt32(), $"{type.Name}.{field.Name} offset");
            }
        }
    }

    [Fact]
    public void GeneratedEnumsMatchTypeManifest()
    {
        using var doc = JsonDocument.Parse(GhosttyRuntime.TypeManifestJson());
        var types = doc.RootElement.GetProperty("types");
        foreach (var name in new[] { "GhosttyResult", "GhosttyTerminalOption", "GhosttyTerminalData", "GhosttyRenderStateData", "GhosttyKey" })
        {
            var type = typeof(GhosttyResult).Assembly.GetType("TKCode.Terminal.Core.Interop." + name)!;
            var values = types.GetProperty(name).GetProperty("values").EnumerateObject().Where(v => !v.Name.EndsWith("MAX_VALUE")).ToList();
            Assert.Equal(values.Count, Enum.GetValues(type).Length);
            foreach (var v in values)
            {
                Assert.Equal(v.Value.GetInt64(), Convert.ToInt64(Enum.Parse(type, v.Name.Replace("_", ""), ignoreCase: true)));
            }
        }
    }
}
