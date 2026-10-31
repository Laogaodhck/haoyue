# 前端 vendor 库

`wwwroot/lib` 下是手工 vendored 的前端库副本，不经 NuGet / npm 管理，版本与完整性完全依赖 [manifest.json](manifest.json)。护栏由 `haoyue_tests/VendoredLibTests.cs` 承担（随 `dotnet test` 在 CI 执行）：

| 校验 | 含义 |
|---|---|
| 哈希 | 清单声明的每个文件 sha256 必须与磁盘一致 |
| 版本下限 | 清单版本不得低于已审计基线（`MinimumVersions`） |
| 版本标记 | 清单声明的版本字符串必须真的出现在对应文件里 |
| 引用覆盖 | `Components/**/*.razor` 与 `wwwroot/js/site.js` 引用的 `lib/...` 必须全部在清单中 |

## 升级流程

1. 从官方发布页下载新版本归档（只认官方源，不要用第三方镜像）。
2. 替换 `wwwroot/lib/<lib>/` 下的文件，保留站点实际引用的子集即可。
3. 更新清单条目的 `version`、`source`、按需更新 `versionMarker`，并重算该库所有文件的 sha256。
4. 运行 `dotnet test haoyue_tests -c Release --filter FullyQualifiedName~VendoredLibTests`，确认哈希、版本标记与引用覆盖全绿。
5. 安全版本升级后，同步上调 `VendoredLibTests.MinimumVersions` 的下限，防止后续回退。

新增一个库 = 新增清单条目 + 登记 `MinimumVersions` + 在 Razor / site.js 中引用 + 跑上述测试。缺任一步 CI 都会失败。

重算哈希（仓库根目录）：

```bash
python - <<'PY'
import hashlib, pathlib
for p in sorted(pathlib.Path('haoyue_webserver/wwwroot/lib').rglob('*')):
    if p.is_file() and p.name != 'manifest.json' and p.suffix != '.md':
        print(p.relative_to('haoyue_webserver/wwwroot/lib').as_posix(), hashlib.sha256(p.read_bytes()).hexdigest())
PY
```
