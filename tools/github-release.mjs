/* GitHub Release 发布脚本：建 release + 上传资产 + 校验
 *
 * 两个仓库都靠它发版（插件包 zip / 本安装器 exe）。
 * 凭据由 gh CLI 提供（先 gh auth login），不再使用环境变量 token。
 * gh release create 自带 --latest/--verify-tag，上传直接走 Upload URL，
 * 发完回读 gh api /releases/latest 确认真接上了。
 *
 * 用法：
 *   node tools/github-release.mjs <repo> <tag> <name> <bodyFile> <assetFile> [contentType] \
 *        [--release-id <id>] [--commitish main]
 *
 *   --release-id   复用已存在的 release（只补资产）
 *   --commitish    tag 还不存在时必填，gh 会从这个 ref 切 tag
 */
import { execFileSync } from "node:child_process";
import fs from "node:fs";

const [repo, tag, name, bodyFile, assetFile, contentType = "application/zip"] = process.argv.slice(2);
if (!repo || !tag || !name || !bodyFile || !assetFile) {
  console.error("args: <repo> <tag> <name> <bodyFile> <assetFile> [contentType]");
  process.exit(1);
}

function gh(args, opts = {}) {
  return execFileSync("gh", args, { encoding: "utf8", ...opts });
}

const body = fs.readFileSync(bodyFile, "utf8");
const assetName = assetFile.split(/[\\/]/).pop();

/* 0. --release-id 复用已存在的 release（只补资产） */
const relIdx = process.argv.indexOf("--release-id");
let rel;
if (relIdx > 0) {
  rel = { id: process.argv[relIdx + 1], tag_name: tag };
  console.log("reuse release id:", rel.id);
} else {
  const cIdx = process.argv.indexOf("--commitish");
  const args = ["release", "create", tag, "-R", repo, "--title", name, "--notes", body, "--verify-tag"];
  if (cIdx > 0) args.push("--target", process.argv[cIdx + 1]);
  /* /releases/latest/download/... 靠 latest 标记解析 */
  args.push("--latest");
  try {
    gh(args);
    console.log("release created:", tag);
  } catch (e) {
    /* tag 已有 release 时幂等复用 */
    if (!String(e.stderr || e.message).includes("already_exists")) throw e;
    console.log("release already exists, reuse");
  }
}

/* 1. 上传资产（--clobber 覆盖同名旧资产） */
gh(["release", "upload", tag, assetFile, "-R", repo, "--clobber"]);
console.log("asset uploaded:", assetName);

/* 2. 回读确认 latest 真的指到本次 tag：命令成功不等于指针生效 */
const latest = JSON.parse(gh(["api", `repos/${repo}/releases/latest`]));
console.log("latest ->", latest.tag_name);
if (latest.tag_name !== tag) {
  console.error("!! latest 仍指向 " + latest.tag_name + "，本次 " + tag + " 没接上");
  process.exit(1);
}
const assets = JSON.parse(gh(["api", `repos/${repo}/releases/tags/${tag}`]));
const found = (assets.assets || []).find((a) => a.name === assetName);
if (!found) { console.error("!! 资产缺失: " + assetName); process.exit(1); }
if (found.size !== fs.statSync(assetFile).size) {
  console.error("!! 资产大小不符: " + found.size + " != " + fs.statSync(assetFile).size);
  process.exit(1);
}
console.log("DONE", JSON.stringify({ id: found.id, asset: assetName, size: found.size }));
