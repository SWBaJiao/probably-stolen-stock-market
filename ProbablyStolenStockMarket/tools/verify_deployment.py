#!/usr/bin/env python3
"""在临时目录验证正式版原生模组部署，不编译 C# 或写入真实游戏。"""

import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET


PROJECT = Path(__file__).resolve().parents[1]


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
    with tempfile.TemporaryDirectory(prefix="native-mod-deployment-") as temporary:
        root = Path(temporary)
        game = root / "Steam/drive_c/Program Files (x86)/Probably Stolen"
        managed = game / "Probably Stolen_Data/Managed"
        managed.mkdir(parents=True)
        (game / "Probably Stolen.exe").write_bytes(b"game fixture")
        # 占位程序集只用于部署与引用存在性检查，不会载入。
        for name in ("Assembly-CSharp.dll", "0Harmony.dll", "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.UI.dll", "Unity.TextMeshPro.dll", "Newtonsoft.Json.dll"):
            (managed / name).write_bytes(b"managed assembly fixture")
        output = root / "output"
        output.mkdir()
        dll = output / "ProbablyStolenStockMarket.dll"
        dll.write_bytes(b"mod fixture; not a real assembly")
        pdb = dll.with_suffix(".pdb")
        pdb.write_bytes(b"symbols fixture")
        assets = root / "Assets"
        sprite = assets / "Textures/Items/stock_market_terminal.png"
        sprite.parent.mkdir(parents=True)
        sprite.write_bytes(b"sprite fixture")
        (assets / ".DS_Store").write_bytes(b"ignored")
        (assets / ".gitkeep").write_bytes(b"")
        manifest = root / "manifest.xml"
        shutil.copyfile(PROJECT / "manifest.xml", manifest)

        # 空 Build 目标只验证 AfterTargets 及失败保护，不执行编译。
        project = root / "DeploymentFixture.proj"
        xml = ET.Element("Project", DefaultTargets="Build")
        group = ET.SubElement(xml, "PropertyGroup")
        ET.SubElement(group, "GameDirectory").text = str(game)
        ET.SubElement(xml, "Import", Project=str(PROJECT / "build/NativeDevelopment.props"))
        properties = ET.SubElement(xml, "PropertyGroup")
        for name, value in {
            "AssemblyName": "ProbablyStolenStockMarket", "TargetName": "ProbablyStolenStockMarket",
            "TargetFileName": dll.name, "TargetPath": dll, "TargetDir": str(output) + "/",
            "ModAssetsDirectory": assets, "ModManifestPath": manifest,
        }.items():
            ET.SubElement(properties, name).text = str(value)
        ET.SubElement(xml, "Target", Name="ResolveAssemblyReferences")
        build = ET.SubElement(xml, "Target", Name="Build")
        ET.SubElement(build, "Error", Condition="'$(FixtureBuildFails)' == 'true'", Text="fixture-build-failed")
        ET.SubElement(xml, "Import", Project=str(PROJECT / "build/NativeDevelopment.targets"))
        ET.ElementTree(xml).write(project, encoding="utf-8", xml_declaration=True)

        installed = game / "Mods/ProbablyStolenStockMarket"
        installed_asset = installed / "Assets/Textures/Items/stock_market_terminal.png"
        config = game / "UserData/ProbablyStolenStockMarket/market-config.json"
        config.parent.mkdir(parents=True)
        config.write_bytes(b"legacy config preserved exactly")
        run(dotnet, project)
        assert (installed / dll.name).read_bytes() == dll.read_bytes()
        assert (installed / "manifest.xml").read_bytes() == manifest.read_bytes()
        assert installed_asset.read_bytes() == sprite.read_bytes()
        assert not (game / "Mods" / dll.name).exists()
        assert not (installed / pdb.name).exists()
        assert not (installed / "Assets/.DS_Store").exists()
        assert not (installed / "Assets/.gitkeep").exists()
        assert not (installed / "0Harmony.dll").exists()
        assert config.read_bytes() == b"legacy config preserved exactly"
        dll.write_bytes(b"updated mod fixture with a different length")
        run(dotnet, project, "-p:DeployDebugSymbols=true")
        assert (installed / dll.name).read_bytes() == dll.read_bytes()
        assert (installed / pdb.name).read_bytes() == pdb.read_bytes()
        print("通过：清单、DLL 和资源部署到原生子目录，文件更新及 PDB 开关有效，旧配置保留。")

        for name, option, expected in (
            ("disabled", "-p:DeployOnBuild=false", None),
            ("design-time", "-p:DesignTimeBuild=true", None),
            ("failed", "-p:FixtureBuildFails=true", "fixture-build-failed"),
        ):
            destination = root / name
            destination.mkdir()
            run(dotnet, project, option, f"-p:GameDirectory={destination}", error=expected)
            assert not (destination / "Mods").exists()
        print("通过：禁用部署、设计时构建和失败构建不复制文件。")

        run(dotnet, project, f"-p:GameDirectory={root / 'missing-game'}", error="自动部署需要包含")
        run(dotnet, project, f"-p:TargetPath={root / 'missing.dll'}", error="找不到")
        run(dotnet, project, "-t:ValidateGameReferences", f"-p:ManagedDirectory={root / 'missing-managed'}", error="缺少")
        run(dotnet, project, "-t:ValidateGameReferences")
        run(dotnet, project, "-t:ValidateModManifest", f"-p:ModManifestPath={root / 'missing-manifest.xml'}", error="缺少原生模组清单")
        incomplete = root / "incomplete.xml"
        manifest_root = ET.fromstring(manifest.read_bytes())
        manifest_root.remove(manifest_root.find("Assemblies"))
        ET.ElementTree(manifest_root).write(incomplete, encoding="utf-8", xml_declaration=True)
        run(dotnet, project, "-t:ValidateModManifest", f"-p:ModManifestPath={incomplete}", error="清单必须准确列出")
        print("通过：缺失游戏引用、编译输出和原生清单时明确报错。")
    print("验证完成；未编译 C#、未安装加载器、未写入真实游戏目录。")


if __name__ == "__main__":
    verify()
