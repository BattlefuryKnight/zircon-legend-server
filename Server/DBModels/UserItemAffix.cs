using Library.SystemModels;
using MirDB;

namespace Server.DBModels
{
    /// <summary>
    /// 装备词条实例：怪物掉落时按概率 roll 出来的具体词条（含已随机的占位符数值），随装备持久化
    /// </summary>
    [UserObject]
    public sealed class UserItemAffix : DBObject
    {
        [Association("Affixes")]
        public UserItem Item
        {
            get { return _Item; }
            set
            {
                if (_Item == value) return;

                var oldValue = _Item;
                _Item = value;

                OnChanged(oldValue, value, "Item");
            }
        }
        private UserItem _Item;

        public ItemAffixInfo Affix
        {
            get { return _Affix; }
            set
            {
                if (_Affix == value) return;

                var oldValue = _Affix;
                _Affix = value;

                OnChanged(oldValue, value, "Affix");
            }
        }
        private ItemAffixInfo _Affix;

        /// <summary>
        /// 已随机的占位符数值，逗号分隔，与配置的 Params 顺序一一对应，如 "30,10"
        /// </summary>
        public string Values
        {
            get { return _Values; }
            set
            {
                if (_Values == value) return;

                var oldValue = _Values;
                _Values = value;

                OnChanged(oldValue, value, "Values");
            }
        }
        private string _Values;

        /// <summary>
        /// roll 时渲染好的最终显示文本（数值已代入模板），供客户端 tooltip 使用
        /// </summary>
        public string DisplayText
        {
            get { return _DisplayText; }
            set
            {
                if (_DisplayText == value) return;

                var oldValue = _DisplayText;
                _DisplayText = value;

                OnChanged(oldValue, value, "DisplayText");
            }
        }
        private string _DisplayText;

        protected override void OnDeleted()
        {
            Item = null;
            Affix = null;
            base.OnDeleted();
        }
    }
}