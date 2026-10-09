# 液滴透镜纹影成像笔记

> 这是预览处理器渲染效果测试样例，覆盖常用 Markdown 语法。

## 1. 基础行内样式

正文支持 **粗体**、*斜体*、~~删除线~~、行内代码 `SIMILARITY_THRESHOLD`，以及自动链接 https://www.sysu.edu.cn 。

## 2. 待办任务

- [x] 同步 Obsidian 笔记到向量库
- [x] 编写 attachment_scanner 附件扫描模块
- [ ] 修复 email_processor_v5 的 IMAP 授权码
- [ ] 博士论文第 3 章的语义分块调优

## 3. 实验参数表

| 参数 | 符号 | 数值 | 备注 |
|:-----|:----:|-----:|------|
| 波长 | λ | 532 nm | 单纵模激光 |
| 液滴半径 | r | 1.2 mm | 去离子水 |
| 折射率 | n | 1.333 | 25 °C |
| 曝光时间 | t | 8 μs | 帧率 4 kHz |

## 4. 代码块

```python
def schlieren_focus(f, n, r):
    """液滴透镜焦距估算（近轴近似）"""
    R = 2 * r                      # 曲率半径
    f_lens = R / (2 * (n - 1))     # 薄透镜公式
    return f_lens + f
```

## 5. 嵌套列表

- 光学平台
  - 纹影系统
    - LED 光源阵列
  - 相机
- 流体控制
  1. 微量注射泵
  2. 电磁阀

## 6. 图片与链接

![液滴透镜示意](assets/sample-image.png)

详细原理参见 [Fresnel 纹影笔记](./相关笔记.md) 与 [光路设计](01-Projects/光路设计.md)。

---

*由 MarkdownPreviewHandler 自动渲染*
