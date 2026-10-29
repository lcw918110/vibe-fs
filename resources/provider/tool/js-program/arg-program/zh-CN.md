必填源码，恰好定义一个继承生成基类 JsProgram 的 Js 类，并实现 async run()。顶层提交 name、program、intent（外加宿主注入的 `scope`）。程序内使用 call(command)、unpark(name,value?)，等待预算只来自工具顶层 intent。
