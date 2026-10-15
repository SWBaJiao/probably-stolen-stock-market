#!/usr/bin/env python3
"""使用临时占位文件验证 MelonLoader 模板的真实部署目标。"""

import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET


TEMPLATE = Path(__file__).resolve().parents[1]


def run(dotnet, project, *arguments, error=None):
    result = subprocess.run(
        [dotnet, "msbuild", str(project), "-nologo", "-verbosity:minimal", *arguments],
        capture_output=True, text=True, timeout=30,
    )
    output = result.stdout + result.stderr
    if error is None and result.returncode:
        raise AssertionError(output)
    if error is not None and (result.returncode == 0 or error not in output):
        raise AssertionError(f"未得到预期错误 {error!r}：\n{output}")


def verify():
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise SystemExit("请先安装 .NET SDK，并确保 dotnet 位于 PATH 中。")
    with tempfile.TemporaryDirectory(prefix="melon-deployment-") as temporary:
        root = Path(temporary)
        game = root / "Steam/drive_c/Program Files (x86)/Probably Stolen"
        runtime = game / "MelonLoader/net6"
        runtime.mkdir(parents=True)
        (runtime / "MelonLoader.dll").write_bytes(b"loader fixture; not a real assembly")
        output = root / "output"
        output.mkdir()
        dll = output / "FixtureMelon.dll"
        dll.write_bytes(b"mod fixture; not a real assembly")
        pdb = output / "FixtureMelon.pdb"
        pdb.write_bytes(b"symbols fixture")
        assets = root / "Assets"
        sprite = assets / "Textures/Items/health_pack.png"
        sprite.parent.mkdir(parents=True)
        sprite.write_bytes(b"sprite fixture")
        (assets / ".DS_Store").write_bytes(b"ignored")
        (assets / ".gitkeep").write_bytes(b"")

        # 空 Build 目标只用于验证 AfterTargets，不执行 C# 编译。
        project = root / "DeploymentFixture.proj"
        xml = ET.Element("Project", DefaultTargets="Build")
        group = ET.SubElement(xml, "PropertyGroup")
        ET.SubElement(group, "GameDirectory").text = str(game)
        ET.SubElement(xml, "Import", Project=str(TEMPLATE / "build/MelonDevelopment.props"))
        properties = ET.SubElement(xml, "PropertyGroup")
        for name, value in {
            "AssemblyName": "FixtureMelon", "TargetName": "FixtureMelon", "TargetFileName": dll.name,
            "TargetPath": dll, "TargetDir": str(output) + "/", "ModAssetsDirectory": assets,
        }.items():
            ET.SubElement(properties, name).text = str(value)
        ET.SubElement(xml, "Target", Name="ResolveAssemblyReferences")
        build = ET.SubElement(xml, "Target", Name="Build")
        ET.SubElement(build, "Error", Condition="'$(FixtureBuildFails)' == 'true'", Text="fixture-build-failed")
        ET.SubElement(xml, "Import", Project=str(TEMPLATE / "build/MelonDevelopment.targets"))
        ET.ElementTree(xml).write(project, encoding="utf-8", xml_declaration=True)

        installed = game / "Mods"
        installed_asset = game / "UserData/FixtureMelon/Assets/Textures/Items/health_pack.png"
        run(dotnet, project)
        assert (installed / dll.name).read_bytes() == dll.read_bytes()
        assert installed_asset.read_bytes() == sprite.read_bytes()
        assert not (installed / "FixtureMelon").exists()
        assert not (installed / "manifest.xml").exists()
        assert not (installed / pdb.name).exists()
        assert not (game / "UserData/FixtureMelon/Assets/.DS_Store").exists()
        assert not (game / "UserData/FixtureMelon/Assets/.gitkeep").exists()
        dll.write_bytes(b"updated mod fixture with a different length")
        run(dotnet, project, "-p:DeployDebugSymbols=true")
        assert (installed / dll.name).read_bytes() == dll.read_bytes()
        assert (installed / pdb.name).read_bytes() == pdb.read_bytes()
        print("通过：DLL 直接复制到 Mods，资源进入 UserData，文件更新及 PDB 开关有效。")

        for name, option, expected in (
            ("disabled", "-p:DeployOnBuild=false", None),
            ("design-time", "-p:DesignTimeBuild=true", None),
            ("failed", "-p:FixtureBuildFails=true", "fixture-build-failed"),
        ):
            destination = root / name
            destination.mkdir()
            run(dotnet, project, option, f"-p:GameDirectory={destination}", error=expected)
            assert not (destination / "Mods").exists()
            assert not (destination / "UserData").exists()
        print("通过：禁用部署、设计时构建和失败构建不会复制文件。")

        run(dotnet, project, f"-p:GameDirectory={root / 'missing-game'}", error="自动部署需要存在的")
        no_loader = root / "no-loader"
        no_loader.mkdir()
        run(dotnet, project, f"-p:GameDirectory={no_loader}", error="目标游戏尚未安装")
        run(dotnet, project, f"-p:TargetPath={root / 'missing.dll'}", error="找不到编译输出")
        run(dotnet, project, "-t:ValidateMelonReferences", f"-p:MelonLoaderAssembly={root / 'missing-loader.dll'}",
            error="找不到 MelonLoader.dll")
        run(dotnet, project, "-t:ValidateGameInterop", "-p:UseGameInterop=true", error="找不到游戏代理")

        interop = game / "MelonLoader/Il2CppAssemblies"
        interop.mkdir()
        (interop / "Il2CppAssembly-CSharp.dll").write_bytes(b"game proxy fixture")
        run(dotnet, project, "-t:ValidateGameInterop", "-p:UseGameInterop=true", error="缺少 IL2CPP 互操作引用")
        (interop / "Il2Cppmscorlib.dll").write_bytes(b"system proxy fixture")
        for name in ("Il2CppInterop.Common.dll", "Il2CppInterop.Runtime.dll"):
            (runtime / name).write_bytes(b"interop runtime fixture")
        run(dotnet, project, "-t:ValidateMelonReferences")
        run(dotnet, project, "-t:ValidateGameInterop", "-p:UseGameInterop=true")
        print("通过：缺失加载器、输出和代理时明确报错，代理引用检查正常。")
    print("验证完成；未编译 C#、未安装加载器、未写入真实游戏目录。")


if __name__ == "__main__":
    verify()
