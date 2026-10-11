# 社区技能提交区

本目录存放社区贡献的技能分发文件，PR 合入后经站点「社区贡献」入口可被浏览与下载。

## 提交结构

```
marketplace/community/<slug>/
├── <slug>.md            # 裸提示词技能，或
├── <slug>.zip           # 含 skill.yaml + prompt.txt 的技能包
└── README.md            # 用途说明 + 至少一个示例对话
```

格式规范与字段说明见 [../CONTRIBUTING.md](../CONTRIBUTING.md)。

## 用户安装

```bash
haoyue skill import marketplace/community/<slug>/<slug>.md   # 或 .zip
```
