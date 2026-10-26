/* CNB Release 发布脚本：建 release + 上传资产 + 校验
 *
 * 两个仓库都靠它发版（插件包 zip / 本安装器 exe）。
 * 凭据取环境变量 CNB_TOKEN（不是 ~/.cnb/config 里的 docker token）。
 * 接口域名必须是 api.cnb.cool，Accept 头必须是 application/vnd.cnb.api+json。
 * 资产上传三步：asset-upload-url（要带 asset_name + size）→ PUT upload_url → POST verify_url。
 * latest 指针用 make_latest:"true"（字符串），create 之后再用 PATCH /releases/{id} 兜一次，
 * 最后回读 GET /releases/latest 确认真接上了 —— is_latest 字段传进去会被静默忽略。
 *
 * 用法：
 *   node tools/cnb-release.mjs <repo> <tag> <name> <bodyFile> <assetFile> [contentType] \
 *        [--release-id <id>] [--commitish refs/heads/main]
 *
 *   --release-id   复用已存在的 release（只补资产）
 *   --commitish    tag 还不存在时必填，Release 接口会顺带从这个 ref 切 tag
 */
import fs from "node:fs";

const TOKEN = process.env.CNB_TOKEN;
if (!TOKEN) { console.error("NO CNB_TOKEN"); process.exit(1); }

const [repo, tag, name, bodyFile, assetFile, contentType = "application/zip"] = process.argv.slice(2);
if (!repo || !tag || !name || !bodyFile || !assetFile) {
  console.error("args: <repo> <tag> <name> <bodyFile> <assetFile> [contentType]");
  process.exit(1);
}
const H = {
  Accept: "application/vnd.cnb.api+json",
  Authorization: "Bearer " + TOKEN,
  "Content-Type": "application/json",
};
const j = async (url, opts) => {
  const r = await fetch(url, opts);
  const text = await r.text();
  let data = null;
  try { data = JSON.parse(text); } catch {}
  if (!r.ok) {
    console.error("HTTP " + r.status + " " + url);
    console.error(text.slice(0, 800));
    process.exit(1);
  }
  return data;
};

const body = fs.readFileSync(bodyFile, "utf8");

/* 1. release */
const relIdx = process.argv.indexOf("--release-id");
let rel;
if (relIdx > 0) {
  rel = { id: process.argv[relIdx + 1], tag_name: tag };
  console.log("reuse release id:", rel.id);
} else {
  const cIdx = process.argv.indexOf("--commitish");
  const payload = {
    tag_name: tag,
    name,
    body,
    draft: false,
    prerelease: false,
    /* /releases/latest/download/... 靠这个标记解析。字段名是 make_latest，
     * 值是字符串（true|false|legacy）—— 传 is_latest:true 会被接口静默吃掉。 */
    make_latest: "true",
  };
  /* tag 还不存在时由 Release 接口顺带建 tag，必须告诉它从哪个 ref 切 */
  if (cIdx > 0) payload.target_commitish = process.argv[cIdx + 1];
  rel = await j(`https://api.cnb.cool/${repo}/-/releases`, {
    method: "POST",
    headers: H,
    body: JSON.stringify(payload),
  });
  console.log("release id:", rel.id, "tag:", rel.tag_name);
}

/* 2. 上传地址 */
const assetName = assetFile.split("/").pop();
const stat = fs.statSync(assetFile);
const up = await j(`https://api.cnb.cool/${repo}/-/releases/${rel.id}/asset-upload-url`, {
  method: "POST",
  headers: H,
  body: JSON.stringify({
    asset_name: assetName,
    hash_algo: "sha256",
    size: stat.size,
    content_type: contentType,
  }),
});
console.log("upload resp keys:", Object.keys(up).join(","));
console.log(JSON.stringify(up).slice(0, 800));

/* 3. PUT 资产 */
const uploadUrl = up.upload_url || up.url || (up.data && (up.data.upload_url || up.data.url));
const verifyUrl = up.verify_url || (up.data && up.data.verify_url);
if (!uploadUrl) { console.error("没有 upload_url"); process.exit(1); }
const buf = fs.readFileSync(assetFile);
const put = await fetch(uploadUrl, {
  method: "PUT",
  headers: { "Content-Type": contentType },
  body: buf,
});
console.log("PUT status:", put.status, put.ok ? "" : (await put.text()).slice(0, 300));
if (!put.ok) process.exit(1);

/* 4. 校验/登记 */
if (verifyUrl) {
  const v = await j(verifyUrl, { method: "POST", headers: H, body: "{}" });
  console.log("verify:", JSON.stringify(v).slice(0, 400));
}

/* 5. --release-id 复用的老 release 当初可能没设上 latest，再 PATCH 一次兜底 */
await j(`https://api.cnb.cool/${repo}/-/releases/${rel.id}`, {
  method: "PATCH",
  headers: H,
  body: JSON.stringify({ make_latest: "true" }),
});

/* 6. 回读确认 latest 真的指到本次 tag：只查接口返回不等于指针生效 */
const latest = await j(`https://api.cnb.cool/${repo}/-/releases/latest`, { headers: H });
console.log("latest ->", latest.tag_name);
if (latest.tag_name !== tag) {
  console.error("!! latest 仍指向 " + latest.tag_name + "，本次 " + tag + " 没接上");
  process.exit(1);
}
console.log("DONE", JSON.stringify({ id: rel.id, asset: assetName }));
