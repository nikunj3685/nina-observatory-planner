using NINA.Astrometry.Interfaces;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Utility.DateTimeProvider;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>The NINA services the planner uses, all of which NINA exports to plugins (see NINA.Plugin.PluginLoader).</summary>
    [Export(typeof(NinaServices))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class NinaServices {

        [ImportingConstructor]
        public NinaServices(IProfileService profileService,
                            ICameraMediator camera,
                            IFilterWheelMediator filterWheel,
                            IFocuserMediator focuser,
                            IRotatorMediator rotator,
                            ITelescopeMediator telescope,
                            IGuiderMediator guider,
                            ISwitchMediator switches,
                            IFlatDeviceMediator flatDevice,
                            IWeatherDataMediator weather,
                            IDomeMediator dome,
                            ISafetyMonitorMediator safetyMonitor,
                            IImagingMediator imaging,
                            IImageSaveMediator imageSave,
                            IImageHistoryVM imageHistory,
                            IApplicationStatusMediator applicationStatus,
                            IApplicationMediator application,
                            INighttimeCalculator nighttimeCalculator,
                            IFramingAssistantVM framingAssistant,
                            IPlanetariumFactory planetariumFactory,
                            IPlateSolverFactory plateSolverFactory,
                            IWindowServiceFactory windowServiceFactory,
                            IDomeFollower domeFollower,
                            IAutoFocusVMFactory autoFocusVMFactory,
                            IMeridianFlipVMFactory meridianFlipVMFactory,
                            ISequenceMediator sequence,
                            IList<IDateTimeProvider> dateTimeProviders) {
            Profile = profileService;
            Camera = camera;
            FilterWheel = filterWheel;
            Focuser = focuser;
            Rotator = rotator;
            Telescope = telescope;
            Guider = guider;
            Switches = switches;
            FlatDevice = flatDevice;
            Weather = weather;
            Dome = dome;
            SafetyMonitor = safetyMonitor;
            Imaging = imaging;
            ImageSave = imageSave;
            ImageHistory = imageHistory;
            ApplicationStatus = applicationStatus;
            Application = application;
            NighttimeCalculator = nighttimeCalculator;
            FramingAssistant = framingAssistant;
            PlanetariumFactory = planetariumFactory;
            PlateSolverFactory = plateSolverFactory;
            WindowServiceFactory = windowServiceFactory;
            DomeFollower = domeFollower;
            AutoFocusVMFactory = autoFocusVMFactory;
            MeridianFlipVMFactory = meridianFlipVMFactory;
            Sequence = sequence;
            DateTimeProviders = dateTimeProviders;
        }

        public IProfileService Profile { get; }
        public ICameraMediator Camera { get; }
        public IFilterWheelMediator FilterWheel { get; }
        public IFocuserMediator Focuser { get; }
        public IRotatorMediator Rotator { get; }
        public ITelescopeMediator Telescope { get; }
        public IGuiderMediator Guider { get; }
        public ISwitchMediator Switches { get; }
        public IFlatDeviceMediator FlatDevice { get; }
        public IWeatherDataMediator Weather { get; }
        public IDomeMediator Dome { get; }
        public ISafetyMonitorMediator SafetyMonitor { get; }
        public IImagingMediator Imaging { get; }
        public IImageSaveMediator ImageSave { get; }
        public IImageHistoryVM ImageHistory { get; }
        public IApplicationStatusMediator ApplicationStatus { get; }
        public IApplicationMediator Application { get; }
        public INighttimeCalculator NighttimeCalculator { get; }
        public IFramingAssistantVM FramingAssistant { get; }
        public IPlanetariumFactory PlanetariumFactory { get; }
        public IPlateSolverFactory PlateSolverFactory { get; }
        public IWindowServiceFactory WindowServiceFactory { get; }
        public IDomeFollower DomeFollower { get; }
        public IAutoFocusVMFactory AutoFocusVMFactory { get; }
        public IMeridianFlipVMFactory MeridianFlipVMFactory { get; }
        public ISequenceMediator Sequence { get; }
        public IList<IDateTimeProvider> DateTimeProviders { get; }
    }
}
