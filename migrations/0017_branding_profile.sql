-- 0017_branding_profile: 品牌配置与个人头像。
--
-- 两件事共用一张 blobs 表（内容寻址），不新建资产表：
--
--   * 品牌 Logo：settings 里的 site_logo_blob_id 指向 blobs.id，mime 存
--     site_logo_mime。替换 Logo 时旧 blob 只解引用（gc 会在 24h 宽限期后回收
--     无用对象），不做同步删除，避免误删被其它节点共享的内容。
--   * 个人头像：users.avatar_blob_id 指向 blobs.id，avatar_mime 存 MIME。
--     头像不参与去重语义上的"共享"：两个用户上传同一张图会命中同一个 blob，
--     引用计数天然正确；删除头像只清空用户列并减引用，由 gc 回收。
--
-- 头像可空（老用户与未上传用户），渲染端回退到用户名首字母色块。外键用
-- ON DELETE SET NULL：gc 删除 blob 行时用户不会被级联删除，只是失去头像。

ALTER TABLE users ADD COLUMN avatar_blob_id INTEGER REFERENCES blobs(id) ON DELETE SET NULL;
ALTER TABLE users ADD COLUMN avatar_mime TEXT NOT NULL DEFAULT '';
