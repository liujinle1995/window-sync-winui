//! 文本差分：把「旧文本 → 新文本」压成一段区间替换。
//!
//! 老实现只认「末尾追加」和「末尾删除」两种形状，其余一律放弃同步 —— 于是
//! 中间插字、移动光标后编辑、全选替换、输入法上屏全都同步不过去。
//!
//! 这里换成通用模型：**先求公共前缀，再求公共后缀，中间剩下的一段就是改动区间**。
//! 上面那些操作只是区间的不同取法而已：
//!
//! | 操作 | 公共前缀 | 改动区间 |
//! |---|---|---|
//! | 末尾追加 `abc` → `abcd` | `abc` | `[3,3)` 换成 `d` |
//! | 末尾删除 | `ab` | `[2,3)` 换成空 |
//! | 中间插入 `ac` → `abc` | `a` | `[1,1)` 换成 `b` |
//! | 全选替换 | 空 | `[0,len)` 换成新文本 |
//! | 输入法上屏 | 光标前的内容 | `[caret,caret+n)` 换成候选词 |
//!
//! 位置与长度一律以 **UTF-16 code unit** 计 —— 和 `EM_GETSEL` / `EM_SETSEL` /
//! `WM_SETTEXT` 的口径一致，免得来回换算。
//!
//! 唯一的坑是代理对：`😀` 在 UTF-16 里占两个 unit，如果前缀边界正好落在两个 unit
//! 中间，切出来的就是非法 UTF-16，从窗口的输入框会直接吞掉半个字。所以边界要往回让，
//! 见 [`splits_pair`]。

/// 一次改动：把 `[at, at + removed)` 这段换成 `inserted`。
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Edit {
    /// 改动起点（UTF-16 下标）
    pub at: usize,
    /// 被替换掉的长度（UTF-16 unit）
    pub removed: usize,
    /// 用来替换的文本
    pub inserted: Vec<u16>,
}

impl Edit {
    /// 这次改动一共动了多少个 unit —— 用来判断走增量还是整段重写
    pub fn size(&self) -> usize {
        self.removed + self.inserted.len()
    }

    /// 改动是否是一次「尾部操作」：末尾追加、末尾删除都属于这种。
    /// 只有这种形状才允许走逐字补（退格/打字），也只有这种形状才敢盲投给读不到文本的从窗口。
    ///
    /// 注意**整段替换不算**：`hello → world` 的改动区间恰好也结束在旧文本末尾，
    /// 但它是从 0 开始删的。这时按「退格 5 次」处理，只要从窗口的光标不在末尾，
    /// 吃掉的就会是别的字。
    pub fn is_tail_only(&self, old_len: usize) -> bool {
        if self.at + self.removed != old_len {
            return false;
        }
        !(self.at == 0 && self.removed > 0)
    }

    /// 只给测试与日志用：把插入内容还原成可读字符串
    #[cfg(test)]
    pub fn inserted_as_string(&self) -> String {
        String::from_utf16_lossy(&self.inserted)
    }
}

fn is_high_surrogate(u: u16) -> bool {
    (0xD800..=0xDBFF).contains(&u)
}

/// 在 `at` 处切开会不会把一个代理对劈成两半
fn splits_pair(s: &[u16], at: usize) -> bool {
    at > 0 && at < s.len() && is_high_surrogate(s[at - 1])
}

/// 求 `old → new` 的改动。文本完全一致时返回 `None`。
pub fn diff(old: &[u16], new: &[u16]) -> Option<Edit> {
    if old == new {
        return None;
    }

    let limit = old.len().min(new.len());

    // 公共前缀
    let mut prefix = 0;
    while prefix < limit && old[prefix] == new[prefix] {
        prefix += 1;
    }
    // 边界落在代理对中间就往回让一个 unit
    while prefix > 0 && (splits_pair(old, prefix) || splits_pair(new, prefix)) {
        prefix -= 1;
    }

    // 公共后缀（前缀之后的部分才允许参与，否则两边会重叠）
    let suffix_limit = limit - prefix;
    let mut suffix = 0;
    while suffix < suffix_limit
        && old[old.len() - 1 - suffix] == new[new.len() - 1 - suffix]
    {
        suffix += 1;
    }
    while suffix > 0
        && (splits_pair(old, old.len() - suffix) || splits_pair(new, new.len() - suffix))
    {
        suffix -= 1;
    }

    Some(Edit {
        at: prefix,
        removed: old.len() - prefix - suffix,
        inserted: new[prefix..new.len() - suffix].to_vec(),
    })
}

/// 把 `&str` 转成 UTF-16 unit 序列，方便和输入框读出来的东西直接比。
#[cfg(test)]
pub fn to_units(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn u(s: &str) -> Vec<u16> {
        to_units(s)
    }

    fn d(old: &str, new: &str) -> Option<Edit> {
        diff(&u(old), &u(new))
    }

    #[test]
    fn identical_text_has_no_edit() {
        assert_eq!(d("乐享同步", "乐享同步"), None);
        assert_eq!(d("", ""), None);
    }

    #[test]
    fn tail_append() {
        let e = d("abc", "abcd").unwrap();
        assert_eq!((e.at, e.removed), (3, 0));
        assert_eq!(e.inserted_as_string(), "d");
        assert!(e.is_tail_only(3));
    }

    #[test]
    fn tail_delete() {
        let e = d("abcd", "ab").unwrap();
        assert_eq!((e.at, e.removed), (2, 2));
        assert!(e.inserted.is_empty());
        assert!(e.is_tail_only(4), "末尾删除也算尾部改动");
        assert_eq!(e.inserted.len(), 0, "末尾删除不该带插入内容");
    }

    /// 光标移到中间插字 —— 老实现直接放弃，这里是主要修复点
    #[test]
    fn insert_in_the_middle() {
        let e = d("ac", "abc").unwrap();
        assert_eq!((e.at, e.removed), (1, 0));
        assert_eq!(e.inserted_as_string(), "b");
        assert!(!e.is_tail_only(2), "中间插入不是尾部改动，不能盲投");
    }

    #[test]
    fn delete_in_the_middle() {
        let e = d("abcd", "acd").unwrap();
        assert_eq!((e.at, e.removed), (1, 1));
        assert!(e.inserted.is_empty());
    }

    /// 全选后输入 —— 前后缀都不共享任何字符。
    /// 这条**不能**被当成尾部删除：从窗口的光标未必在末尾。
    #[test]
    fn replace_everything() {
        let e = d("hello", "world").unwrap();
        assert_eq!((e.at, e.removed), (0, 5));
        assert_eq!(e.inserted_as_string(), "world");
        assert!(!e.is_tail_only(5), "整段替换不能走逐字退格");
    }

    /// 清空全部内容同理
    #[test]
    fn clearing_everything_is_not_a_tail_edit() {
        let e = d("abc", "").unwrap();
        assert_eq!((e.at, e.removed), (0, 3));
        assert!(!e.is_tail_only(3));
    }

    /// 往空输入框里打字是合法的尾部追加
    #[test]
    fn typing_into_an_empty_box_is_a_tail_edit() {
        let e = d("", "乐享").unwrap();
        assert!(e.is_tail_only(0));
    }

    /// 中间一段等长替换，比如输入法把拼音换成汉字
    #[test]
    fn replace_a_middle_run() {
        let e = d("hello world", "hello there").unwrap();
        assert_eq!(e.at, 6);
        assert_eq!(e.removed, 5);
        assert_eq!(e.inserted_as_string(), "there");
    }

    /// 两处不相邻的改动会被合并成一个大区间 —— 结果仍然正确，只是效率低一点
    #[test]
    fn disjoint_changes_collapse_into_one_range() {
        let e = d("abcXdef", "abcYdefZ").unwrap();
        assert_eq!(e.at, 3);
        assert_eq!(e.removed, 4, "前缀后缀之外的整段都要算进改动区间");
        assert_eq!(e.inserted_as_string(), "YdefZ");
    }

    #[test]
    fn empty_to_text_and_back() {
        let e = d("", "乐享").unwrap();
        assert_eq!((e.at, e.removed), (0, 0));
        assert_eq!(e.inserted_as_string(), "乐享");

        let e = d("乐享", "").unwrap();
        assert_eq!((e.at, e.removed), (0, 2));
    }

    /// 代理对整体替换：绝不切开半个 emoji
    #[test]
    fn surrogate_pair_is_replaced_whole() {
        let e = d("a😀b", "a😃b").unwrap();
        assert_eq!(e.at, 1, "边界必须让到 emoji 之前");
        assert_eq!(e.removed, 2, "整个代理对一起换掉");
        assert_eq!(e.inserted, u("😃"), "换进来的也必须是完整代理对");

        // 改动后拼回去必须和原文一致
        let old = u("a😀b");
        let new = u("a😃b");
        let mut rebuilt = old[..e.at].to_vec();
        rebuilt.extend_from_slice(&e.inserted);
        rebuilt.extend_from_slice(&old[e.at + e.removed..]);
        assert_eq!(rebuilt, new);
    }

    /// 插入内容本身就是代理对
    #[test]
    fn inserting_an_emoji() {
        let e = d("ab", "a😀b").unwrap();
        assert_eq!((e.at, e.removed), (1, 0));
        assert_eq!(e.inserted, u("😀"));
    }

    /// 首字符不同、后缀整段共享时，公共后缀里允许包含完整的 emoji
    /// （只有**劈开**代理对才需要让步，整个代理对落在后缀里是合法的）
    #[test]
    fn shared_suffix_may_contain_a_whole_surrogate_pair() {
        let e = d("x😀", "y😀").unwrap();
        assert_eq!(e.at, 0);
        assert_eq!(e.removed, 1, "只换掉首字母，emoji 留在后缀里");
        assert_eq!(e.inserted, u("y"));

        // 边界必须落在合法位置：被切掉的前缀不能以高代理结尾
        let old = u("x😀");
        assert!(!(e.at > 0 && (0xD800..=0xDBFF).contains(&old[e.at - 1])));
    }

    /// 任意改动都能用 diff 的结果还原出新文本 —— 这是全模块最该守住的契约
    #[test]
    fn every_edit_reconstructs_the_new_text() {
        let cases = [
            ("", ""),
            ("", "a"),
            ("a", ""),
            ("abc", "abcd"),
            ("abcd", "abc"),
            ("abc", "axc"),
            ("中文字", "中日文字"),
            ("中文字", "中文字"),
            ("hello world", "hello there"),
            ("a😀b", "a😃b"),
            ("😀", "😀😀"),
            ("1234567890", "1234507890"),
        ];

        for (old, new) in cases {
            let old_u = u(old);
            let new_u = u(new);
            match diff(&old_u, &new_u) {
                Some(e) => {
                    assert!(e.at + e.removed <= old_u.len(), "{old} → {new}: 区间越界");
                    assert!(!e.inserted_as_string().contains('\u{FFFD}'), "{old} → {new}: 切出了非法 UTF-16");
                    let mut rebuilt = old_u[..e.at].to_vec();
                    rebuilt.extend_from_slice(&e.inserted);
                    rebuilt.extend_from_slice(&old_u[e.at + e.removed..]);
                    assert_eq!(rebuilt, new_u, "{old} → {new}: 还原失败");
                }
                None => assert_eq!(old, new, "{old} → {new}: 文本不同却没有给出改动"),
            }
        }
    }
}
