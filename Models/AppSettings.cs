using PenguinMonitor.Services;
using PenguinMonitor.UI.Factories;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Android.Renderscripts.Sampler;
using Newtonsoft.Json;


namespace PenguinMonitor.Models
{
    public class AppSettings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnAnyPropertyChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
        }
        public string filesDir;
        public AppSettings(string filesDir)
        {
            this.filesDir = filesDir;
        }
        private string allBoxSetsString;
        public string AllBoxSetsString
        {
            get => allBoxSetsString;
            set
            {
                if (allBoxSetsString != value)
                {
                    allBoxSetsString = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private string boxSetString;
        public string BoxSetString
        {
            get => boxSetString;
            set
            {
                if (boxSetString != value)
                {
                    boxSetString = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool isBluetoothEnabled;
        public bool IsBlueToothEnabled
        {
            get => isBluetoothEnabled;
            set
            {
                if (isBluetoothEnabled != value)
                {
                    isBluetoothEnabled = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showMultiboxFilterCard;
        public bool ShowMultiboxFilterCard
        {
            get => showMultiboxFilterCard;
            set
            {
                if (showMultiboxFilterCard != value)
                {
                    showMultiboxFilterCard = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showAllBoxesInMultiBoxView;
        public bool ShowAllBoxesInMultiBoxView
        {
            get => showAllBoxesInMultiBoxView;
            set
            {
                if (showAllBoxesInMultiBoxView != value)
                {
                    showAllBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showBoxesWithDataInMultiBoxView;
        public bool ShowBoxesWithDataInMultiBoxView
        {
            get => showBoxesWithDataInMultiBoxView;
            set
            {
                if (showBoxesWithDataInMultiBoxView != value)
                {
                    showBoxesWithDataInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showNoBoxesInMultiBoxView;
        public bool ShowNoBoxesInMultiBoxView
        {
            get => showNoBoxesInMultiBoxView;
            set
            {
                if (showNoBoxesInMultiBoxView != value)
                {
                    showNoBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showUnlikleyBoxesInMultiBoxView;
        public bool ShowUnlikleyBoxesInMultiBoxView
        {
            get => showUnlikleyBoxesInMultiBoxView;
            set
            {
                if (showUnlikleyBoxesInMultiBoxView != value)
                {
                    showUnlikleyBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showPotentialBoxesInMultiBoxView;
        public bool ShowPotentialBoxesInMultiBoxView
        {
            get => showPotentialBoxesInMultiBoxView;
            set
            {
                if (showPotentialBoxesInMultiBoxView != value)
                {
                    showPotentialBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showConfidentBoxesInMultiBoxView;
        public bool ShowConfidentBoxesInMultiBoxView
        {
            get => showConfidentBoxesInMultiBoxView;
            set
            {
                if (showConfidentBoxesInMultiBoxView != value)
                {
                    showConfidentBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showBreedingBoxesInMultiBoxView;
        public bool ShowBreedingBoxesInMultiBoxView 
        { 
            get => showBreedingBoxesInMultiBoxView;
            set
            {
                if (showBreedingBoxesInMultiBoxView != value)
                {
                    showBreedingBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        internal bool showBoxesWithNotesInMultiboxView;
        public bool ShowBoxesWithNotesInMultiboxView
        {
            get => showBoxesWithNotesInMultiboxView;
            set
            {
                if (showBoxesWithNotesInMultiboxView != value)
                {
                    showBoxesWithNotesInMultiboxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showInterestingBoxesInMultiBoxView;
        public bool ShowInterestingBoxesInMultiBoxView 
        {
            get => showInterestingBoxesInMultiBoxView;
            set
            {
                if (showInterestingBoxesInMultiBoxView != value)
                {
                    showInterestingBoxesInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool showSingleEggBoxesInMultiboxView;
        public bool ShowSingleEggBoxesInMultiboxView
        {
            get => showSingleEggBoxesInMultiboxView;
            set
            {
                if (showSingleEggBoxesInMultiboxView != value)
                {
                    showSingleEggBoxesInMultiboxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool hideBoxesWithDataInMultiBoxView;  
        public bool HideBoxesWithDataInMultiBoxView
        {
            get => hideBoxesWithDataInMultiBoxView;
            set
            {
                if (hideBoxesWithDataInMultiBoxView != value)
                {
                    hideBoxesWithDataInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool hideDCMInMultiBoxView;
        public bool HideDCMInMultiBoxView
        {
            get => hideDCMInMultiBoxView;
            set
            {
                if (hideDCMInMultiBoxView != value)
                {
                    hideDCMInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private int currentlyVisibleMonitor = 0;
        public int CurrentlyVisibleMonitor
        {
            get => currentlyVisibleMonitor;
            set
            {
                if (currentlyVisibleMonitor != value)
                {
                    currentlyVisibleMonitor = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private DateTime _activeSessionLocalTimeStamp;
        public DateTime ActiveSessionLocalTimeStamp
        {
            get => _activeSessionLocalTimeStamp;
            set
            {
                if (_activeSessionLocalTimeStamp != value)
                {
                    _activeSessionLocalTimeStamp = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool activeSessionTimeStampActive;
        public bool ActiveSessionTimeStampActive
        {
            get => activeSessionTimeStampActive;
            set
            {
                if (activeSessionTimeStampActive != value)
                {
                    activeSessionTimeStampActive = value;
                    OnAnyPropertyChanged();
                }
            }
        }
        private bool hideBeforeCurrentInMultiBoxView;
        public bool HideBeforeCurrentInMultiBoxView {
            get => hideBeforeCurrentInMultiBoxView;
            set
            {
                if (hideBeforeCurrentInMultiBoxView != value)
                {
                    hideBeforeCurrentInMultiBoxView = value;
                    OnAnyPropertyChanged();
                }
            }
        }
    }
}
