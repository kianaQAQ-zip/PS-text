using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PSText.Infrastructure
{
    /// <summary>
    /// 所有 ViewModel 的基类：实现 INotifyPropertyChanged。
    /// 说明：不引用任何 UI 类型，便于单元测试。
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 触发属性变更通知。
        /// </summary>
        /// <param name="propertyName">属性名；由编译器自动填充。</param>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
            catch (Exception ex)
            {
                // Win7 上个别绑定控件可能在数据模板重建期间抛异常，这里兜底以免整个 UI 线程崩溃。
                System.Diagnostics.Debug.WriteLine("[ObservableObject] 属性通知异常 " + propertyName + ": " + ex);
            }
        }

        /// <summary>
        /// 设置字段值；若发生变化则赋值、触发通知并返回 true。
        /// </summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
