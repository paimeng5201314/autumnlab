# 派蒙的个人代码签名申请衔接

2026-10-24。派蒙已确认申请主体为中国大陆个人，使用本人真实身份资料；目前没有代码签名证书。应用署名继续是“制作人：派蒙”。本文件没有向任何机构发送，也未下单或提交个人资料。

## 当前能否直接签

尚不能完成受信签名。项目内微软 SignTool 已安装且其签名为Valid；实际运行 verify /pa /all 检查当前交付EXE得到退出码1、No signature found。两个本机My证书存储均无代码签名证书。现有工具本身不能替代签发机构的身份核验与证书。

本地预检报告：`signing-readiness-d170345d454f4581b34a5744eca4daaf/report.json`，含25个产品命名PE的待审查清单（这不是自动批准签署第三方组件的名单）。未签署文件、未生成自签根、未改证书存储/系统策略。

## 与本地签名要求相符的候选

- [Certum Standard Code Signing](https://www.certum.eu/en/code-signing-certificates/)：官方明确支持自然人；可核查密码卡＋USB读卡器方案。[个人验证材料要求](https://support.certum.eu/en/code-signing-required-documents/)包括身份证明和本人名下地址证明。购买前请官方确认大陆证件、地址材料及寄送的实际支持。官网存在个人方案不等于这个订单已获准签发。
- [SSL.com IV Code Signing](https://www.ssl.com/products/software-integrity/code-signing/iv/)：官方明确无需注册公司，提供实体YubiKey选项及[本地SignTool操作](https://www.ssl.com/how-to/using-your-code-signing-certificate/)。购买前同样确认大陆个人的验证方式与硬件寄送，总价应包含证书、硬件、运费及税费。

这两条是待确认候选，不是已获批准的订单；未承诺成功签发或给出未经确认的交货时间。实体硬件签名可在本机处理EXE，时间戳/证书状态检查仍可能联网，不上传工程源码。证书颁发时的个人资料由本人在签发机构官方流程提交。

[Microsoft Artifact Signing quickstart](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) 当前明确Public Trust个人仅限美国、加拿大，中国大陆个人不适用。不能按应用受众改填其他地区，也不能用Private Trust或自签证书冒充公众信任证书。

## 可复制给官方售前的询问（未发送）

> I am an individual software developer residing in mainland China, applying with my own Chinese identity documents. I need a publicly trusted Authenticode code-signing certificate for a Windows desktop application. I do not have a registered company. Can you issue an individual certificate in this case? Please confirm the accepted ID and proof-of-address documents, any translation/notarization requirements, physical token/card delivery to mainland China, and the full first-year cost including hardware, shipping, and taxes. I need to sign EXE/DLL files locally with Microsoft SignTool without uploading the binaries. Please also confirm the publisher name that will appear in the certificate and the refund terms if identity validation cannot be completed.

中文要点：大陆自然人、没有企业主体；确认公众信任代码签名证书、证件与地址证明、翻译/公证、实体硬件寄送、首年总价、实名发布者显示及验证失败退款条件；要求本地SignTool，不上传EXE。此询问本身不需要附身份证或地址文件。

## 拿到证书后的操作分工

派蒙本人完成官方身份验证、付款和硬件PIN输入。身份证、私钥、PIN、账户密码不要发到聊天。个人证书的Windows发布者显示依据核验身份，可能显示真实姓名；软件界面的制作人署名仍可保留派蒙。

代理继续负责：核对证书用途/链/有效期与实际硬件访问；在新构建副本先签自有内层EXE/DLL，验签后重建receipt/inventory/payload，再签外层EXE；时间戳、最终哈希、EXE/ZIP解包与签名核验。本轮没有实施这些依赖证书的步骤，不能标为passed。GUI复验继续按派蒙要求由其实际检测，依据真实结果判断SAC是否接受。

完整签名顺序及现有隔离缺口见工程 `docs/windows-startup-signing.md`。签名不等于第三方沙箱通过，ChemLeap普通模式的隔离阻塞仍保留。
