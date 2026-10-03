(function () {
  "use strict";
  sample.bind("refresh", async () => {
    const value = await sample.request("lifecycle.getState");
    sample.text("state-output", "state=" + value.state + "；blocksMaintenance=" + value.blocksMaintenance);
    sample.status("已读取当前实例；返回桌面仍会阻止主程序替换。");
  });
  sample.start();
})();
