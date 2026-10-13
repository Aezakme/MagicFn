// UMDF2 HID minidriver for the keyboard interface (MI_01) of the Magic Keyboard with Touch ID, USB-C (05AC:0321).
// It replaces hidusb: reads the interface through the UMDF native USB dispatcher and hands the reports to HIDClass
// through mshidumdf, which sits above as an upper filter.
// On the way it does what Apple's Boot Camp driver KeyMagic2 does for an external keyboard (Fn layer, media keys),
// and adds what that driver lacks: the Lock button, as a vendor-defined collection a program can subscribe to.
#include <windows.h>
#include <wdf.h>
#include <usb.h>
#include <wdfusb.h>

#ifndef USBD_CLIENT_CONTRACT_VERSION_602
#define USBD_CLIENT_CONTRACT_VERSION_602 0x602   // lives in the kernel-only usbdlib.h
#endif

#define HID_CTL(id) CTL_CODE(FILE_DEVICE_KEYBOARD, (id), METHOD_NEITHER, FILE_ANY_ACCESS)
#define IOCTL_HID_GET_DEVICE_DESCRIPTOR     HID_CTL(0)
#define IOCTL_HID_GET_REPORT_DESCRIPTOR     HID_CTL(1)
#define IOCTL_HID_READ_REPORT               HID_CTL(2)
#define IOCTL_HID_WRITE_REPORT              HID_CTL(3)
#define IOCTL_HID_ACTIVATE_DEVICE           HID_CTL(7)
#define IOCTL_HID_DEACTIVATE_DEVICE         HID_CTL(8)
#define IOCTL_HID_GET_DEVICE_ATTRIBUTES     HID_CTL(9)
#define IOCTL_UMDF_HID_SET_FEATURE          HID_CTL(20)
#define IOCTL_UMDF_HID_GET_FEATURE          HID_CTL(21)
#define IOCTL_UMDF_HID_SET_OUTPUT_REPORT    HID_CTL(22)
#define IOCTL_UMDF_HID_GET_INPUT_REPORT     HID_CTL(23)

#define HID_DESCRIPTOR_TYPE 0x21
#define HID_REPORT_DESCRIPTOR_TYPE 0x22
#define HID_GET_REPORT 0x01
#define HID_SET_REPORT 0x09
#define HID_REPORT_INPUT 1
#define HID_REPORT_OUTPUT 2
#define HID_REPORT_FEATURE 3

#pragma pack(push, 1)
typedef struct { BYTE bLength, bDescriptorType; USHORT bcdHID; BYTE bCountry, bNumDescriptors, bReportType; USHORT wReportLength; } HID_DESCRIPTOR;
#pragma pack(pop)
typedef struct { ULONG Size; USHORT VendorID, ProductID, VersionNumber, Reserved[11]; } HID_DEVICE_ATTRIBUTES;
C_ASSERT(sizeof(HID_DESCRIPTOR) == 9);
C_ASSERT(sizeof(HID_DEVICE_ATTRIBUTES) == 32);

// Keyboard report: [0] id=1, [1] modifiers, [2] reserved, [3..8] keys, [9] bits: 0 Eject, 1 Fn, 2 Lock
#define KEYBOARD_REPORT_ID 1
#define KEYBOARD_REPORT_LEN 10
#define KEYBOARD_MODIFIERS 1
#define KEYS_FIRST 3
#define KEYS_LAST 8
#define KEYBOARD_BUTTONS 9
#define FN_BIT 0x02
#define LOCK_BIT 0x04
#define LEFT_CTRL 0x01
#define LEFT_SHIFT 0x02
#define LEFT_ALT 0x04

#define MEDIA_REPORT_ID 0x53   // same id and layout as KeyMagic2's media collection: one consumer usage, 0 = released
#define LOCK_REPORT_ID 0x54

static const BYTE MediaCollection[] = {
    0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, MEDIA_REPORT_ID, 0x95, 0x01, 0x75, 0x08, 0x15, 0x00, 0x25, 0xFF,
    0x19, 0x00, 0x29, 0xFF, 0x81, 0x00, 0xC0 };

// Vendor page FF00, usage 0x10: the keys that have no key code Windows would act on. Input: one byte of APP_* bits,
// set while the key is down. Feature (usage 0x11): one byte, 1 = top row sends media keys without Fn and F1-F12 with Fn.
// These keys are not sent as keystrokes on purpose: Windows kept F16 and Win+Tab pressed after the release whenever
// the press had fired a hotkey.
static const BYTE LockCollection[] = {
    0x06, 0x00, 0xFF, 0x09, 0x10, 0xA1, 0x01, 0x85, LOCK_REPORT_ID, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x01,
    0x09, 0x10, 0x81, 0x02, 0x09, 0x11, 0xB1, 0x02, 0xC0 };
#define APP_LOCK 0x01

// Fn layer: key -> key. Same pairs as KeyMagic2.
static const BYTE FnKeys[][2] = {
    { 0x2A, 0x4C },   // Backspace -> Delete
    { 0x28, 0x49 },   // Enter -> Insert
    { 0x58, 0x49 },   // keypad Enter -> Insert
    { 0x29, 0x48 },   // Esc -> Pause
    { 0x4F, 0x4D },   // Right -> End
    { 0x50, 0x4A },   // Left -> Home
    { 0x51, 0x4E },   // Down -> Page Down
    { 0x52, 0x4B } }; // Up -> Page Up

// Top row: key -> consumer usage. Same pairs as KeyMagic2, which leaves F3-F6 alone: they have no usage of their own.
static const BYTE MediaKeys[][2] = {
    { 0x3A, 0x70 },   // F1 -> brightness down
    { 0x3B, 0x6F },   // F2 -> brightness up
    { 0x40, 0xB6 },   // F7 -> previous track
    { 0x41, 0xCD },   // F8 -> play/pause
    { 0x42, 0xB5 },   // F9 -> next track
    { 0x43, 0xE2 },   // F10 -> mute
    { 0x44, 0xEA },   // F11 -> volume down
    { 0x45, 0xE9 } }; // F12 -> volume up
#define KEY_F11 0x44
#define KEY_F12 0x45
#define KEY_BACKSPACE 0x2A
#define KEY_DELETE 0x4C
#define KEY_PRINT_SCREEN 0x46
#define KEY_SCROLL_LOCK 0x47

// Top row keys without a usage of their own: key -> bit in the app report. MagicFn gives them their meaning.
static const BYTE AppKeys[][2] = {
    { 0x3C, 0x02 },   // F3, Mission Control
    { 0x3D, 0x04 },   // F4, Spotlight
    { 0x3E, 0x08 },   // F5, Dictation
    { 0x3F, 0x10 } }; // F6, Do Not Disturb

#define MAX_REPORT 128          // longest input report of this interface is 65 bytes
#define MAX_REPORT_DESCRIPTOR 1024
#define PENDING_REPORTS 16

typedef struct { BYTE Data[MAX_REPORT]; size_t Length; } REPORT;

typedef struct {
    WDFUSBDEVICE Usb;
    WDFUSBPIPE Interrupt;
    BYTE InterfaceNumber;
    HID_DESCRIPTOR Hid;
    HID_DEVICE_ATTRIBUTES Attributes;
    BYTE ReportDescriptor[MAX_REPORT_DESCRIPTOR];
    ULONG ReportDescriptorLength;
    WDFQUEUE Reads;             // IOCTL_HID_READ_REPORT requests waiting for a report
    WDFSPINLOCK Lock;           // guards Reads against Pending: a report must never be lost between the two
    REPORT Pending[PENDING_REPORTS];
    ULONG PendingHead, PendingCount;
    BYTE AppState;              // APP_* bits last sent in the app report
    BYTE TopRowMedia;           // set through the feature report
    BYTE MediaKey, MediaUsage;  // top-row key currently held as a media key: hidden until released, whatever Fn does
    BYTE AppKey, AppBit;        // top-row key currently held as an app key: hidden until released, whatever Fn does
} DEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD OnDeviceAdd;
EVT_WDF_DEVICE_PREPARE_HARDWARE OnPrepareHardware;
EVT_WDF_DEVICE_D0_ENTRY OnD0Entry;
EVT_WDF_DEVICE_D0_EXIT OnD0Exit;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL OnDeviceControl;
EVT_WDF_USB_READER_COMPLETION_ROUTINE OnInterrupt;
EVT_WDF_USB_READERS_FAILED OnReadersFailed;

NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, OnDeviceAdd);
    NTSTATUS status = WdfDriverCreate(driver, registry, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
    return status;
}

NTSTATUS OnDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT init)
{
    UNREFERENCED_PARAMETER(driver);
    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDevicePrepareHardware = OnPrepareHardware;
    pnp.EvtDeviceD0Entry = OnD0Entry;
    pnp.EvtDeviceD0Exit = OnD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(init, &pnp);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    WDFDEVICE device;
    NTSTATUS status = WdfDeviceCreate(&init, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;
    DEVICE_CONTEXT* context = GetContext(device);

    status = WdfSpinLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &context->Lock);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG queue;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue, WdfIoQueueDispatchParallel);
    queue.EvtIoDeviceControl = OnDeviceControl;
    status = WdfIoQueueCreate(device, &queue, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG_INIT(&queue, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(device, &queue, WDF_NO_OBJECT_ATTRIBUTES, &context->Reads);
    return status;
}

static NTSTATUS Control(DEVICE_CONTEXT* context, BOOLEAN toHost, BOOLEAN classRequest, BYTE request, USHORT value,
                        void* buffer, ULONG length, ULONG* transferred)
{
    WDF_USB_BMREQUEST_DIRECTION direction = toHost ? BmRequestDeviceToHost : BmRequestHostToDevice;
    WDF_USB_CONTROL_SETUP_PACKET setup;
    if (classRequest) WDF_USB_CONTROL_SETUP_PACKET_INIT_CLASS(&setup, direction, BmRequestToInterface, request, value, context->InterfaceNumber);
    else WDF_USB_CONTROL_SETUP_PACKET_INIT(&setup, direction, BmRequestToInterface, request, value, context->InterfaceNumber);

    WDF_MEMORY_DESCRIPTOR memory;
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&memory, buffer, length);
    WDF_REQUEST_SEND_OPTIONS options;
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&options, WDF_REL_TIMEOUT_IN_SEC(2));
    return WdfUsbTargetDeviceSendControlTransferSynchronously(context->Usb, WDF_NO_HANDLE, &options, &setup, &memory, transferred);
}

// The HID descriptor follows this interface's descriptor inside the configuration descriptor.
static void FindHidDescriptor(DEVICE_CONTEXT* context)
{
    HID_DESCRIPTOR fallback = { sizeof(HID_DESCRIPTOR), HID_DESCRIPTOR_TYPE, 0x0110, 0, 1, HID_REPORT_DESCRIPTOR_TYPE, 0 };
    context->Hid = fallback;

    BYTE config[1024];
    USHORT size = sizeof config;
    if (!NT_SUCCESS(WdfUsbTargetDeviceRetrieveConfigDescriptor(context->Usb, config, &size))) return;
    BOOLEAN inInterface = FALSE;
    for (USHORT i = 0; i + 2 <= size && config[i] >= 2 && i + config[i] <= size; i = (USHORT)(i + config[i]))
    {
        if (config[i + 1] == USB_INTERFACE_DESCRIPTOR_TYPE) inInterface = config[i] >= 3 && config[i + 2] == context->InterfaceNumber;
        else if (inInterface && config[i + 1] == HID_DESCRIPTOR_TYPE && config[i] >= sizeof(HID_DESCRIPTOR))
        {
            memcpy(&context->Hid, config + i, sizeof(HID_DESCRIPTOR));
            context->Hid.bLength = sizeof(HID_DESCRIPTOR);
            context->Hid.bNumDescriptors = 1;
            return;
        }
    }
}

NTSTATUS OnPrepareHardware(WDFDEVICE device, WDFCMRESLIST raw, WDFCMRESLIST translated)
{
    UNREFERENCED_PARAMETER(raw);
    UNREFERENCED_PARAMETER(translated);
    DEVICE_CONTEXT* context = GetContext(device);
    NTSTATUS status;

    if (context->Usb == NULL)
    {
        WDF_USB_DEVICE_CREATE_CONFIG create;
        WDF_USB_DEVICE_CREATE_CONFIG_INIT(&create, USBD_CLIENT_CONTRACT_VERSION_602);
        status = WdfUsbTargetDeviceCreateWithParameters(device, &create, WDF_NO_OBJECT_ATTRIBUTES, &context->Usb);
        if (!NT_SUCCESS(status)) return status;
    }

    WDF_USB_DEVICE_SELECT_CONFIG_PARAMS select;
    WDF_USB_DEVICE_SELECT_CONFIG_PARAMS_INIT_SINGLE_INTERFACE(&select);
    status = WdfUsbTargetDeviceSelectConfig(context->Usb, WDF_NO_OBJECT_ATTRIBUTES, &select);
    if (!NT_SUCCESS(status)) return status;
    WDFUSBINTERFACE usbInterface = select.Types.SingleInterface.ConfiguredUsbInterface;
    context->InterfaceNumber = WdfUsbInterfaceGetInterfaceNumber(usbInterface);

    context->Interrupt = NULL;
    WDF_USB_PIPE_INFORMATION pipeInfo = { 0 };
    for (BYTE i = 0; i < WdfUsbInterfaceGetNumConfiguredPipes(usbInterface); i++)
    {
        WDF_USB_PIPE_INFORMATION_INIT(&pipeInfo);
        WDFUSBPIPE pipe = WdfUsbInterfaceGetConfiguredPipe(usbInterface, i, &pipeInfo);
        if (pipeInfo.PipeType == WdfUsbPipeTypeInterrupt && WdfUsbTargetPipeIsInEndpoint(pipe)) { context->Interrupt = pipe; break; }
    }
    if (context->Interrupt == NULL) return STATUS_DEVICE_CONFIGURATION_ERROR;

    USB_DEVICE_DESCRIPTOR deviceDescriptor;
    WdfUsbTargetDeviceGetDeviceDescriptor(context->Usb, &deviceDescriptor);
    context->Attributes.Size = sizeof(HID_DEVICE_ATTRIBUTES);
    context->Attributes.VendorID = deviceDescriptor.idVendor;
    context->Attributes.ProductID = deviceDescriptor.idProduct;
    context->Attributes.VersionNumber = deviceDescriptor.bcdDevice;

    FindHidDescriptor(context);

    ULONG length = 0;
    ULONG room = MAX_REPORT_DESCRIPTOR - sizeof MediaCollection - sizeof LockCollection;
    status = Control(context, TRUE, FALSE, USB_REQUEST_GET_DESCRIPTOR, HID_REPORT_DESCRIPTOR_TYPE << 8, context->ReportDescriptor, room, &length);
    if (!NT_SUCCESS(status)) return status;
    if (length == 0) return STATUS_DEVICE_DATA_ERROR;
    memcpy(context->ReportDescriptor + length, MediaCollection, sizeof MediaCollection);
    length += sizeof MediaCollection;
    memcpy(context->ReportDescriptor + length, LockCollection, sizeof LockCollection);
    length += sizeof LockCollection;
    context->ReportDescriptorLength = length;
    context->Hid.wReportLength = (USHORT)length;

    WDF_USB_CONTINUOUS_READER_CONFIG reader;
    WDF_USB_CONTINUOUS_READER_CONFIG_INIT(&reader, OnInterrupt, context, MAX_REPORT);
    // One read at a time. With the default of two, their completions run on separate threads and can overtake each
    // other: a key release processed before its press leaves the key stuck down in Windows. Fn chords, which send
    // two reports a few milliseconds apart, hit this constantly.
    reader.NumPendingReads = 1;
    reader.EvtUsbTargetPipeReadersFailed = OnReadersFailed;
    status = WdfUsbTargetPipeConfigContinuousReader(context->Interrupt, &reader);
    return status;
}

NTSTATUS OnD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE previous)
{
    UNREFERENCED_PARAMETER(previous);
    NTSTATUS status = WdfIoTargetStart(WdfUsbTargetPipeGetIoTarget(GetContext(device)->Interrupt));
    return status;
}

NTSTATUS OnD0Exit(WDFDEVICE device, WDF_POWER_DEVICE_STATE target)
{
    UNREFERENCED_PARAMETER(target);
    WdfIoTargetStop(WdfUsbTargetPipeGetIoTarget(GetContext(device)->Interrupt), WdfIoTargetCancelSentIo);
    return STATUS_SUCCESS;
}

BOOLEAN OnReadersFailed(WDFUSBPIPE pipe, NTSTATUS status, USBD_STATUS usbdStatus)
{
    UNREFERENCED_PARAMETER(pipe);
    UNREFERENCED_PARAMETER(status);
    UNREFERENCED_PARAMETER(usbdStatus);
    return TRUE;   // let the framework reset the pipe and restart the readers
}

static void CompleteWithData(WDFREQUEST request, const void* data, size_t length)
{
    WDFMEMORY memory;
    NTSTATUS status = WdfRequestRetrieveOutputMemory(request, &memory);
    if (NT_SUCCESS(status))
    {
        size_t capacity;
        WdfMemoryGetBuffer(memory, &capacity);
        status = capacity < length ? STATUS_INVALID_BUFFER_SIZE : WdfMemoryCopyFromBuffer(memory, 0, (PVOID)data, length);
    }
    WdfRequestCompleteWithInformation(request, status, NT_SUCCESS(status) ? length : 0);
}

// Hands a report to HIDClass: to a waiting read if there is one, otherwise it waits for the next read.
static void Deliver(DEVICE_CONTEXT* context, const BYTE* data, size_t length)
{
    WDFREQUEST request = NULL;
    WdfSpinLockAcquire(context->Lock);
    if (!NT_SUCCESS(WdfIoQueueRetrieveNextRequest(context->Reads, &request)))
    {
        request = NULL;
        if (context->PendingCount < PENDING_REPORTS)
        {
            REPORT* slot = &context->Pending[(context->PendingHead + context->PendingCount++) % PENDING_REPORTS];
            memcpy(slot->Data, data, length);
            slot->Length = length;
        }
        // else: HIDClass has stopped reading; reports are dropped as they would be on the wire
    }
    WdfSpinLockRelease(context->Lock);
    if (request) CompleteWithData(request, data, length);
}

static void ReadReport(DEVICE_CONTEXT* context, WDFREQUEST request)
{
    REPORT report = { 0 };
    BOOLEAN have = FALSE;
    NTSTATUS status = STATUS_SUCCESS;
    WdfSpinLockAcquire(context->Lock);
    if (context->PendingCount > 0)
    {
        report = context->Pending[context->PendingHead];
        context->PendingHead = (context->PendingHead + 1) % PENDING_REPORTS;
        context->PendingCount--;
        have = TRUE;
    }
    else status = WdfRequestForwardToIoQueue(request, context->Reads);
    WdfSpinLockRelease(context->Lock);
    if (have) CompleteWithData(request, report.Data, report.Length);
    else if (!NT_SUCCESS(status)) WdfRequestComplete(request, status);
}

static BYTE Lookup(const BYTE table[][2], size_t count, BYTE key)
{
    for (size_t i = 0; i < count; i++) if (table[i][0] == key) return table[i][1];
    return 0;
}

// Rewrites a keyboard report in place and returns the consumer usage the top row holds down, 0 for none.
static BYTE ApplyFnLayer(DEVICE_CONTEXT* context, BYTE* report)
{
    BOOLEAN fn = (report[KEYBOARD_BUTTONS] & FN_BIT) != 0;
    BOOLEAN mediaLayer = fn != (context->TopRowMedia != 0);
    BYTE* modifiers = &report[KEYBOARD_MODIFIERS];
    BOOLEAN mediaKeyStillDown = FALSE, appKeyStillDown = FALSE;

    for (int i = KEYS_FIRST; i <= KEYS_LAST; i++)
    {
        BYTE key = report[i];
        if (key == 0) continue;
        if (key == context->MediaKey) { report[i] = 0; mediaKeyStillDown = TRUE; continue; }
        if (key == context->AppKey) { report[i] = 0; appKeyStillDown = TRUE; continue; }

        if (key == KEY_BACKSPACE && *modifiers == (LEFT_CTRL | LEFT_ALT)) { report[i] = KEY_DELETE; continue; }   // Ctrl+Alt+Del
        if (fn)
        {
            // Fn+Shift+F11 is Print Screen (with Alt kept for a window shot), Fn+Shift+F12 is Scroll Lock
            if (key == KEY_F11 && (*modifiers == LEFT_SHIFT || *modifiers == (LEFT_SHIFT | LEFT_ALT))) { *modifiers &= ~LEFT_SHIFT; report[i] = KEY_PRINT_SCREEN; continue; }
            if (key == KEY_F12 && *modifiers == LEFT_SHIFT) { *modifiers = 0; report[i] = KEY_SCROLL_LOCK; continue; }
            BYTE mapped = Lookup(FnKeys, ARRAYSIZE(FnKeys), key);
            if (mapped) { report[i] = mapped; continue; }
        }
        if (mediaLayer && context->MediaKey == 0)
        {
            BYTE usage = Lookup(MediaKeys, ARRAYSIZE(MediaKeys), key);
            if (usage) { context->MediaKey = key; context->MediaUsage = usage; report[i] = 0; mediaKeyStillDown = TRUE; continue; }
        }
        if (mediaLayer && context->AppKey == 0)
        {
            BYTE bit = Lookup(AppKeys, ARRAYSIZE(AppKeys), key);
            if (bit) { context->AppKey = key; context->AppBit = bit; report[i] = 0; appKeyStillDown = TRUE; }
        }
    }
    if (!mediaKeyStillDown) context->MediaKey = context->MediaUsage = 0;
    if (!appKeyStillDown) context->AppKey = context->AppBit = 0;
    return context->MediaUsage;
}

void OnInterrupt(WDFUSBPIPE pipe, WDFMEMORY buffer, size_t length, WDFCONTEXT rawContext)
{
    UNREFERENCED_PARAMETER(pipe);
    DEVICE_CONTEXT* context = (DEVICE_CONTEXT*)rawContext;
    BYTE* data = (BYTE*)WdfMemoryGetBuffer(buffer, NULL);
    if (length == 0 || length > MAX_REPORT) return;

    if (length != KEYBOARD_REPORT_LEN || data[0] != KEYBOARD_REPORT_ID)
    {
        Deliver(context, data, length);
        return;
    }

    BYTE buttons = data[KEYBOARD_BUTTONS];
    BYTE mediaBefore = context->MediaUsage;
    BYTE media = ApplyFnLayer(context, data);
    Deliver(context, data, length);

    if (media != mediaBefore)
    {
        BYTE mediaReport[2] = { MEDIA_REPORT_ID, media };
        Deliver(context, mediaReport, sizeof mediaReport);
    }
    BYTE appState = (BYTE)(((buttons & LOCK_BIT) ? APP_LOCK : 0) | context->AppBit);
    if (appState != context->AppState)
    {
        context->AppState = appState;
        BYTE appReport[2] = { LOCK_REPORT_ID, appState };
        Deliver(context, appReport, sizeof appReport);
    }
}

// Report data arrives in the input buffer and starts with the report id.
static void SetReport(DEVICE_CONTEXT* context, WDFREQUEST request, BYTE type)
{
    BYTE* data;
    size_t length;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(request, 1, (PVOID*)&data, &length);
    if (NT_SUCCESS(status))
    {
        if (data[0] == LOCK_REPORT_ID)   // our own collection, not the keyboard's
        {
            if (type == HID_REPORT_FEATURE && length >= 2) { context->TopRowMedia = data[1] != 0; }
            else status = STATUS_INVALID_PARAMETER;
        }
        else status = Control(context, FALSE, TRUE, HID_SET_REPORT, (USHORT)(type << 8 | data[0]), data, (ULONG)length, NULL);
    }
    WdfRequestComplete(request, status);
}

// The report id arrives in the input buffer, the report goes to the output buffer.
static void GetReport(DEVICE_CONTEXT* context, WDFREQUEST request, BYTE type)
{
    BYTE* id;
    BYTE* data = NULL;
    size_t capacity = 0;
    ULONG transferred = 0;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(request, 1, (PVOID*)&id, NULL);
    if (NT_SUCCESS(status)) status = WdfRequestRetrieveOutputBuffer(request, 2, (PVOID*)&data, &capacity);
    if (NT_SUCCESS(status))
    {
        if (*id == LOCK_REPORT_ID)
        {
            data[0] = LOCK_REPORT_ID;
            data[1] = type == HID_REPORT_FEATURE ? context->TopRowMedia : context->AppState;
            transferred = 2;
        }
        else status = Control(context, TRUE, TRUE, HID_GET_REPORT, (USHORT)(type << 8 | *id), data, (ULONG)capacity, &transferred);
    }
    WdfRequestCompleteWithInformation(request, status, NT_SUCCESS(status) ? transferred : 0);
}

void OnDeviceControl(WDFQUEUE queue, WDFREQUEST request, size_t outputLength, size_t inputLength, ULONG code)
{
    DEVICE_CONTEXT* context = GetContext(WdfIoQueueGetDevice(queue));
    UNREFERENCED_PARAMETER(outputLength);
    UNREFERENCED_PARAMETER(inputLength);
    switch (code)
    {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR: CompleteWithData(request, &context->Hid, sizeof context->Hid); break;
    case IOCTL_HID_GET_DEVICE_ATTRIBUTES: CompleteWithData(request, &context->Attributes, sizeof context->Attributes); break;
    case IOCTL_HID_GET_REPORT_DESCRIPTOR: CompleteWithData(request, context->ReportDescriptor, context->ReportDescriptorLength); break;
    case IOCTL_HID_READ_REPORT: ReadReport(context, request); break;
    case IOCTL_HID_WRITE_REPORT:
    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT: SetReport(context, request, HID_REPORT_OUTPUT); break;
    case IOCTL_UMDF_HID_SET_FEATURE: SetReport(context, request, HID_REPORT_FEATURE); break;
    case IOCTL_UMDF_HID_GET_FEATURE: GetReport(context, request, HID_REPORT_FEATURE); break;
    case IOCTL_UMDF_HID_GET_INPUT_REPORT: GetReport(context, request, HID_REPORT_INPUT); break;
    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE: WdfRequestComplete(request, STATUS_SUCCESS); break;
    // shortcut: device strings and idle notification are not served, so HidD_GetProductString fails and the
    // keyboard never selectively suspends; add IOCTL_HID_GET_STRING if a program needs the names.
    default: WdfRequestComplete(request, STATUS_NOT_IMPLEMENTED); break;
    }
}
