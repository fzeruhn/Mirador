// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0
//
// xr-check: Mirador's small OpenXR test client (Mirador.md §4.11 item 49, "Verify build for Windows").
//
//   xr-check --api d3d11|vulkan|none [--frames 300] [--timeout 60]
//
// --api none only creates an instance (the runtime loads and its service takes the app): no graphics, no session.
//
// Makes an instance, a session and one swapchain per view with the graphics API asked for, runs the frames (each image
// acquired, waited for and released, a projection layer submitted), ends the session, and prints one result line:
//
//   XR-CHECK {"api":"d3d11","ok":true,"frames":300,"submitted":300,"runtime":"...","system":"...","error":""}
//
// A frame the runtime says not to render, or whose views have no valid pose, ends without a layer; "submitted" counts
// the frames that rendered one, and fewer than half of them fails the check.
// Exit code 0 when every frame was ended and enough rendered, 1 when a step failed, 2 for a bad command line, 3 on the
// timeout.
// The runtime is the one the OpenXR loader finds (XR_RUNTIME_JSON, else the machine's active runtime).

#include <windows.h>
#include <unknwn.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <vulkan/vulkan.h>

#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_VULKAN
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {

struct Result
{
	std::string api;
	bool ok = false;
	int frames = 0;
	int submitted = 0; // frames that submitted a projection layer (rendered), of `frames`
	std::string runtime;
	std::string system;
	std::string error;
};

std::string
json_escape(const std::string &text)
{
	std::string out;
	for (const unsigned char c : text) {
		switch (c) {
		case '"': out += "\\\""; break;
		case '\\': out += "\\\\"; break;
		case '\n': out += "\\n"; break;
		case '\r': out += "\\r"; break;
		case '\t': out += "\\t"; break;
		default:
			if (c < 0x20) {
				char buffer[8];
				std::snprintf(buffer, sizeof(buffer), "\\u%04x", c);
				out += buffer;
			} else {
				out += static_cast<char>(c);
			}
		}
	}
	return out;
}

void
print(const Result &r)
{
	std::printf("XR-CHECK {\"api\":\"%s\",\"ok\":%s,\"frames\":%d,\"submitted\":%d,\"runtime\":\"%s\",\"system\":\"%s\",\"error\":\"%s\"}\n",
	            json_escape(r.api).c_str(), r.ok ? "true" : "false", r.frames, r.submitted, json_escape(r.runtime).c_str(),
	            json_escape(r.system).c_str(), json_escape(r.error).c_str());
	std::fflush(stdout);
}

struct Failure : std::runtime_error
{
	using std::runtime_error::runtime_error;
};

XrInstance g_instance = XR_NULL_HANDLE;

//! Frames submitted so far, for the watchdog (which must not read the result's strings while they change).
std::atomic<int> g_frames{0};

std::string
describe(XrResult result)
{
	char name[XR_MAX_RESULT_STRING_SIZE] = {};
	if (g_instance != XR_NULL_HANDLE && XR_SUCCEEDED(xrResultToString(g_instance, result, name))) {
		return name;
	}
	return "XrResult " + std::to_string(static_cast<int>(result));
}

void
check(XrResult result, const char *step)
{
	if (XR_FAILED(result)) {
		throw Failure(std::string(step) + " failed: " + describe(result));
	}
}

template <typename F>
F
proc(const char *name)
{
	PFN_xrVoidFunction function = nullptr;
	check(xrGetInstanceProcAddr(g_instance, name, &function), name);
	return reinterpret_cast<F>(function);
}

//! What one graphics API gives the session: the binding, the swapchain image type, and the clean-up.
struct Graphics
{
	const void *binding = nullptr;
	XrStructureType image_type = XR_TYPE_UNKNOWN;
	size_t image_size = 0;
	virtual ~Graphics() = default;
};

struct D3D11Graphics : Graphics
{
	ID3D11Device *device = nullptr;
	ID3D11DeviceContext *context = nullptr;
	XrGraphicsBindingD3D11KHR d3d11_binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};

	D3D11Graphics(XrSystemId system)
	{
		auto get_requirements = proc<PFN_xrGetD3D11GraphicsRequirementsKHR>("xrGetD3D11GraphicsRequirementsKHR");
		XrGraphicsRequirementsD3D11KHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
		check(get_requirements(g_instance, system, &requirements), "xrGetD3D11GraphicsRequirementsKHR");

		IDXGIFactory1 *factory = nullptr;
		if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void **>(&factory)))) {
			throw Failure("CreateDXGIFactory1 failed");
		}
		IDXGIAdapter1 *adapter = nullptr;
		for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
			DXGI_ADAPTER_DESC1 desc{};
			adapter->GetDesc1(&desc);
			if (std::memcmp(&desc.AdapterLuid, &requirements.adapterLuid, sizeof(LUID)) == 0) {
				break;
			}
			adapter->Release();
			adapter = nullptr;
		}
		factory->Release();
		if (adapter == nullptr) {
			throw Failure("the GPU the runtime asked for (its LUID) isn't one DXGI lists");
		}

		const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
		const HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
		                                     levels, 2, D3D11_SDK_VERSION, &device, nullptr, &context);
		adapter->Release();
		if (FAILED(hr)) {
			throw Failure("D3D11CreateDevice failed (HRESULT " + std::to_string(static_cast<long>(hr)) + ")");
		}

		d3d11_binding.device = device;
		binding = &d3d11_binding;
		image_type = XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR;
		image_size = sizeof(XrSwapchainImageD3D11KHR);
	}

	~D3D11Graphics() override
	{
		if (context != nullptr) {
			context->Release();
		}
		if (device != nullptr) {
			device->Release();
		}
	}
};

struct VulkanGraphics : Graphics
{
	HMODULE library = nullptr;
	PFN_vkGetInstanceProcAddr get_proc = nullptr;
	VkInstance vk_instance = VK_NULL_HANDLE;
	VkDevice vk_device = VK_NULL_HANDLE;
	XrGraphicsBindingVulkan2KHR vk_binding{XR_TYPE_GRAPHICS_BINDING_VULKAN2_KHR};

	VulkanGraphics(XrSystemId system)
	{
		library = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
		if (library == nullptr) {
			throw Failure("vulkan-1.dll isn't installed (the GPU driver brings it)");
		}
		get_proc = reinterpret_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(library, "vkGetInstanceProcAddr"));
		if (get_proc == nullptr) {
			throw Failure("vulkan-1.dll has no vkGetInstanceProcAddr");
		}

		auto get_requirements = proc<PFN_xrGetVulkanGraphicsRequirements2KHR>("xrGetVulkanGraphicsRequirements2KHR");
		XrGraphicsRequirementsVulkan2KHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_VULKAN2_KHR};
		check(get_requirements(g_instance, system, &requirements), "xrGetVulkanGraphicsRequirements2KHR");

		VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
		app.pApplicationName = "xr-check";
		app.apiVersion = VK_API_VERSION_1_1;
		VkInstanceCreateInfo instance_info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
		instance_info.pApplicationInfo = &app;

		XrVulkanInstanceCreateInfoKHR xr_instance_info{XR_TYPE_VULKAN_INSTANCE_CREATE_INFO_KHR};
		xr_instance_info.systemId = system;
		xr_instance_info.pfnGetInstanceProcAddr = get_proc;
		xr_instance_info.vulkanCreateInfo = &instance_info;
		VkResult vk_result = VK_SUCCESS;
		auto create_instance = proc<PFN_xrCreateVulkanInstanceKHR>("xrCreateVulkanInstanceKHR");
		check(create_instance(g_instance, &xr_instance_info, &vk_instance, &vk_result), "xrCreateVulkanInstanceKHR");
		if (vk_result != VK_SUCCESS) {
			throw Failure("vkCreateInstance failed (VkResult " + std::to_string(vk_result) + ")");
		}

		XrVulkanGraphicsDeviceGetInfoKHR device_get{XR_TYPE_VULKAN_GRAPHICS_DEVICE_GET_INFO_KHR};
		device_get.systemId = system;
		device_get.vulkanInstance = vk_instance;
		VkPhysicalDevice physical = VK_NULL_HANDLE;
		auto get_device = proc<PFN_xrGetVulkanGraphicsDevice2KHR>("xrGetVulkanGraphicsDevice2KHR");
		check(get_device(g_instance, &device_get, &physical), "xrGetVulkanGraphicsDevice2KHR");

		auto get_families = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(
		    get_proc(vk_instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
		uint32_t count = 0;
		get_families(physical, &count, nullptr);
		std::vector<VkQueueFamilyProperties> families(count);
		get_families(physical, &count, families.data());
		uint32_t family = UINT32_MAX;
		for (uint32_t i = 0; i < count; i++) {
			if ((families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0) {
				family = i;
				break;
			}
		}
		if (family == UINT32_MAX) {
			throw Failure("the runtime's Vulkan GPU has no graphics queue");
		}

		const float priority = 1.0f;
		VkDeviceQueueCreateInfo queue_info{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
		queue_info.queueFamilyIndex = family;
		queue_info.queueCount = 1;
		queue_info.pQueuePriorities = &priority;
		VkDeviceCreateInfo device_info{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
		device_info.queueCreateInfoCount = 1;
		device_info.pQueueCreateInfos = &queue_info;

		XrVulkanDeviceCreateInfoKHR xr_device_info{XR_TYPE_VULKAN_DEVICE_CREATE_INFO_KHR};
		xr_device_info.systemId = system;
		xr_device_info.pfnGetInstanceProcAddr = get_proc;
		xr_device_info.vulkanPhysicalDevice = physical;
		xr_device_info.vulkanCreateInfo = &device_info;
		auto create_device = proc<PFN_xrCreateVulkanDeviceKHR>("xrCreateVulkanDeviceKHR");
		check(create_device(g_instance, &xr_device_info, &vk_device, &vk_result), "xrCreateVulkanDeviceKHR");
		if (vk_result != VK_SUCCESS) {
			throw Failure("vkCreateDevice failed (VkResult " + std::to_string(vk_result) + ")");
		}

		vk_binding.instance = vk_instance;
		vk_binding.physicalDevice = physical;
		vk_binding.device = vk_device;
		vk_binding.queueFamilyIndex = family;
		vk_binding.queueIndex = 0;
		binding = &vk_binding;
		image_type = XR_TYPE_SWAPCHAIN_IMAGE_VULKAN2_KHR;
		image_size = sizeof(XrSwapchainImageVulkan2KHR);
	}

	~VulkanGraphics() override
	{
		if (vk_device != VK_NULL_HANDLE) {
			reinterpret_cast<PFN_vkDestroyDevice>(get_proc(vk_instance, "vkDestroyDevice"))(vk_device, nullptr);
		}
		if (vk_instance != VK_NULL_HANDLE) {
			reinterpret_cast<PFN_vkDestroyInstance>(get_proc(vk_instance, "vkDestroyInstance"))(vk_instance, nullptr);
		}
		if (library != nullptr) {
			FreeLibrary(library);
		}
	}
};

//! Polls the session's events; returns its state after them.
XrSessionState
poll(XrSessionState state)
{
	XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
	while (xrPollEvent(g_instance, &event) == XR_SUCCESS) {
		if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
			state = reinterpret_cast<XrEventDataSessionStateChanged *>(&event)->state;
		} else if (event.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) {
			throw Failure("the runtime is going away (instance loss pending)");
		}
		event = {XR_TYPE_EVENT_DATA_BUFFER};
	}
	return state;
}

XrSessionState
wait_for(XrSessionState state, XrSessionState wanted, const char *what)
{
	const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(15);
	while (state != wanted) {
		state = poll(state);
		if (state == XR_SESSION_STATE_EXITING || state == XR_SESSION_STATE_LOSS_PENDING) {
			throw Failure(std::string("the session ended while waiting for it to be ") + what);
		}
		if (std::chrono::steady_clock::now() > until) {
			throw Failure(std::string("the session wasn't ") + what + " within 15 s");
		}
		std::this_thread::sleep_for(std::chrono::milliseconds(10));
	}
	return state;
}

// --api none: the runtime loads and its service takes the app (xrCreateInstance), with no graphics API and no session.
// The nightly's check on CI, whose machines have no GPU for Monado's compositor (Mirador_Plan.md).
void
run_instance_only(Result &result)
{
	XrInstanceCreateInfo create{XR_TYPE_INSTANCE_CREATE_INFO};
	strcpy_s(create.applicationInfo.applicationName, "xr-check");
	create.applicationInfo.applicationVersion = 1;
	strcpy_s(create.applicationInfo.engineName, "Mirador");
	create.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
	check(xrCreateInstance(&create, &g_instance), "xrCreateInstance");

	XrInstanceProperties instance_props{XR_TYPE_INSTANCE_PROPERTIES};
	check(xrGetInstanceProperties(g_instance, &instance_props), "xrGetInstanceProperties");
	result.runtime = std::string(instance_props.runtimeName) + " " + std::to_string(XR_VERSION_MAJOR(instance_props.runtimeVersion)) + "." +
	                 std::to_string(XR_VERSION_MINOR(instance_props.runtimeVersion)) + "." +
	                 std::to_string(XR_VERSION_PATCH(instance_props.runtimeVersion));
	result.ok = true;
}

void
run(Result &result, bool vulkan, int frames)
{
	const char *extension = vulkan ? XR_KHR_VULKAN_ENABLE2_EXTENSION_NAME : XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
	XrInstanceCreateInfo create{XR_TYPE_INSTANCE_CREATE_INFO};
	strcpy_s(create.applicationInfo.applicationName, "xr-check");
	create.applicationInfo.applicationVersion = 1;
	strcpy_s(create.applicationInfo.engineName, "Mirador");
	create.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
	create.enabledExtensionCount = 1;
	create.enabledExtensionNames = &extension;
	check(xrCreateInstance(&create, &g_instance), "xrCreateInstance");

	XrInstanceProperties instance_props{XR_TYPE_INSTANCE_PROPERTIES};
	check(xrGetInstanceProperties(g_instance, &instance_props), "xrGetInstanceProperties");
	result.runtime = std::string(instance_props.runtimeName) + " " + std::to_string(XR_VERSION_MAJOR(instance_props.runtimeVersion)) + "." +
	                 std::to_string(XR_VERSION_MINOR(instance_props.runtimeVersion)) + "." +
	                 std::to_string(XR_VERSION_PATCH(instance_props.runtimeVersion));

	XrSystemGetInfo system_info{XR_TYPE_SYSTEM_GET_INFO};
	system_info.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
	XrSystemId system = XR_NULL_SYSTEM_ID;
	check(xrGetSystem(g_instance, &system_info, &system), "xrGetSystem");
	XrSystemProperties system_props{XR_TYPE_SYSTEM_PROPERTIES};
	check(xrGetSystemProperties(g_instance, system, &system_props), "xrGetSystemProperties");
	result.system = system_props.systemName;

	Graphics *graphics = vulkan ? static_cast<Graphics *>(new VulkanGraphics(system)) : new D3D11Graphics(system);
	XrSession session = XR_NULL_HANDLE;
	XrSpace space = XR_NULL_HANDLE;
	std::vector<XrSwapchain> swapchains;
	try {
		XrSessionCreateInfo session_info{XR_TYPE_SESSION_CREATE_INFO};
		session_info.next = graphics->binding;
		session_info.systemId = system;
		check(xrCreateSession(g_instance, &session_info, &session), "xrCreateSession");

		XrReferenceSpaceCreateInfo space_info{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
		space_info.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
		space_info.poseInReferenceSpace.orientation.w = 1.0f;
		check(xrCreateReferenceSpace(session, &space_info, &space), "xrCreateReferenceSpace");

		uint32_t view_count = 0;
		const XrViewConfigurationType config = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
		check(xrEnumerateViewConfigurationViews(g_instance, system, config, 0, &view_count, nullptr), "xrEnumerateViewConfigurationViews");
		std::vector<XrViewConfigurationView> views(view_count, {XR_TYPE_VIEW_CONFIGURATION_VIEW});
		check(xrEnumerateViewConfigurationViews(g_instance, system, config, view_count, &view_count, views.data()),
		      "xrEnumerateViewConfigurationViews");

		uint32_t format_count = 0;
		check(xrEnumerateSwapchainFormats(session, 0, &format_count, nullptr), "xrEnumerateSwapchainFormats");
		std::vector<int64_t> formats(format_count);
		check(xrEnumerateSwapchainFormats(session, format_count, &format_count, formats.data()), "xrEnumerateSwapchainFormats");
		if (formats.empty()) {
			throw Failure("the runtime offers no swapchain format");
		}

		for (const auto &view : views) {
			XrSwapchainCreateInfo swapchain_info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
			swapchain_info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
			swapchain_info.format = formats[0];
			swapchain_info.sampleCount = 1;
			swapchain_info.width = view.recommendedImageRectWidth;
			swapchain_info.height = view.recommendedImageRectHeight;
			swapchain_info.faceCount = 1;
			swapchain_info.arraySize = 1;
			swapchain_info.mipCount = 1;
			XrSwapchain swapchain = XR_NULL_HANDLE;
			check(xrCreateSwapchain(session, &swapchain_info, &swapchain), "xrCreateSwapchain");
			swapchains.push_back(swapchain);

			uint32_t image_count = 0;
			check(xrEnumerateSwapchainImages(swapchain, 0, &image_count, nullptr), "xrEnumerateSwapchainImages");
			std::vector<unsigned char> images(graphics->image_size * image_count);
			for (uint32_t i = 0; i < image_count; i++) {
				auto *header = reinterpret_cast<XrSwapchainImageBaseHeader *>(images.data() + i * graphics->image_size);
				header->type = graphics->image_type;
			}
			check(xrEnumerateSwapchainImages(swapchain, image_count, &image_count,
			                                 reinterpret_cast<XrSwapchainImageBaseHeader *>(images.data())),
			      "xrEnumerateSwapchainImages");
		}

		XrSessionState state = wait_for(XR_SESSION_STATE_UNKNOWN, XR_SESSION_STATE_READY, "ready");
		XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO};
		begin.primaryViewConfigurationType = config;
		check(xrBeginSession(session, &begin), "xrBeginSession");

		for (int frame = 0; frame < frames; frame++) {
			state = poll(state);
			if (state == XR_SESSION_STATE_STOPPING || state == XR_SESSION_STATE_EXITING || state == XR_SESSION_STATE_LOSS_PENDING) {
				throw Failure("the session ended after " + std::to_string(frame) + " frames");
			}

			XrFrameState frame_state{XR_TYPE_FRAME_STATE};
			check(xrWaitFrame(session, nullptr, &frame_state), "xrWaitFrame");
			check(xrBeginFrame(session, nullptr), "xrBeginFrame");

			std::vector<XrCompositionLayerProjectionView> projection_views(view_count, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW});
			XrCompositionLayerProjection layer{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
			const XrCompositionLayerBaseHeader *layers[] = {reinterpret_cast<XrCompositionLayerBaseHeader *>(&layer)};
			uint32_t layer_count = 0;
			if (frame_state.shouldRender) {
				std::vector<XrView> located(view_count, {XR_TYPE_VIEW});
				XrViewLocateInfo locate{XR_TYPE_VIEW_LOCATE_INFO};
				locate.viewConfigurationType = config;
				locate.displayTime = frame_state.predictedDisplayTime;
				locate.space = space;
				XrViewState view_state{XR_TYPE_VIEW_STATE};
				uint32_t located_count = 0;
				check(xrLocateViews(session, &locate, &view_state, view_count, &located_count, located.data()), "xrLocateViews");

				// A layer needs valid poses; without them (no tracking yet) the frame ends empty.
				if ((view_state.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) != 0) {
					for (uint32_t v = 0; v < view_count; v++) {
						uint32_t index = 0;
						check(xrAcquireSwapchainImage(swapchains[v], nullptr, &index), "xrAcquireSwapchainImage");
						XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
						wait.timeout = 1000000000; // 1 s
						const XrResult waited = xrWaitSwapchainImage(swapchains[v], &wait);
						check(waited, "xrWaitSwapchainImage");
						if (waited == XR_TIMEOUT_EXPIRED) {
							throw Failure("a swapchain image wasn't ready within 1 s (xrWaitSwapchainImage timed out)");
						}
						check(xrReleaseSwapchainImage(swapchains[v], nullptr), "xrReleaseSwapchainImage");

						projection_views[v].pose = located[v].pose;
						projection_views[v].fov = located[v].fov;
						projection_views[v].subImage.swapchain = swapchains[v];
						projection_views[v].subImage.imageRect.extent = {
						    static_cast<int32_t>(views[v].recommendedImageRectWidth),
						    static_cast<int32_t>(views[v].recommendedImageRectHeight)};
					}
					layer.space = space;
					layer.viewCount = view_count;
					layer.views = projection_views.data();
					layer_count = 1;
				}
			}

			XrFrameEndInfo end{XR_TYPE_FRAME_END_INFO};
			end.displayTime = frame_state.predictedDisplayTime;
			end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
			end.layerCount = layer_count;
			end.layers = layers;
			check(xrEndFrame(session, &end), "xrEndFrame");
			result.frames = frame + 1;
			result.submitted += static_cast<int>(layer_count);
			g_frames = frame + 1;
		}

		// A frame that never rendered (the session never visible, no valid pose) proves nothing about the graphics
		// path: at least half the frames must have submitted their layer.
		if (result.submitted * 2 < result.frames) {
			throw Failure("only " + std::to_string(result.submitted) + " of " + std::to_string(result.frames) +
			              " frames rendered a layer (the session wasn't visible, or the views had no valid pose)");
		}

		// Monado moves the session to STOPPING through its frames: keep ending empty frames until it does (5 s at most).
		check(xrRequestExitSession(session), "xrRequestExitSession");
		const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(5);
		while ((state = poll(state)) != XR_SESSION_STATE_STOPPING) {
			if (state == XR_SESSION_STATE_EXITING || state == XR_SESSION_STATE_LOSS_PENDING) {
				throw Failure("the session ended instead of stopping");
			}
			if (std::chrono::steady_clock::now() > until) {
				throw Failure("the session didn't stop within 5 s of the exit request");
			}
			XrFrameState frame_state{XR_TYPE_FRAME_STATE};
			check(xrWaitFrame(session, nullptr, &frame_state), "xrWaitFrame");
			check(xrBeginFrame(session, nullptr), "xrBeginFrame");
			XrFrameEndInfo end{XR_TYPE_FRAME_END_INFO};
			end.displayTime = frame_state.predictedDisplayTime;
			end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
			check(xrEndFrame(session, &end), "xrEndFrame");
		}
		check(xrEndSession(session), "xrEndSession");
		result.ok = true;
	} catch (...) {
		for (auto swapchain : swapchains) {
			xrDestroySwapchain(swapchain);
		}
		if (space != XR_NULL_HANDLE) {
			xrDestroySpace(space);
		}
		if (session != XR_NULL_HANDLE) {
			xrDestroySession(session);
		}
		delete graphics;
		throw;
	}

	for (auto swapchain : swapchains) {
		xrDestroySwapchain(swapchain);
	}
	xrDestroySpace(space);
	xrDestroySession(session);
	delete graphics;
}

int
usage(const char *why)
{
	std::fprintf(stderr, "xr-check: %s\nusage: xr-check --api d3d11|vulkan|none [--frames N] [--timeout S]\n", why);
	return 2;
}

} // namespace

int
main(int argc, char **argv)
{
	std::string api;
	int frames = 300;
	int timeout = 60;
	for (int i = 1; i < argc; i++) {
		const std::string arg = argv[i];
		if (i + 1 >= argc) {
			return usage(("missing a value after " + arg).c_str());
		}
		const std::string value = argv[++i];
		if (arg == "--api") {
			api = value;
		} else if (arg == "--frames") {
			frames = std::atoi(value.c_str());
		} else if (arg == "--timeout") {
			timeout = std::atoi(value.c_str());
		} else {
			return usage(("unknown option " + arg).c_str());
		}
	}
	if (api != "d3d11" && api != "vulkan" && api != "none") {
		return usage("--api must be d3d11, vulkan or none");
	}
	if (frames < 1 || frames > 100000 || timeout < 1 || timeout > 3600) {
		return usage("--frames must be 1 to 100000 and --timeout 1 to 3600");
	}

	// A runtime that hangs (a service that never answers) still gives a result line.
	static Result result;
	result.api = api;
	static std::atomic<bool> done{false};
	std::thread([timeout, api] {
		const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(timeout);
		while (!done.load()) {
			if (std::chrono::steady_clock::now() > until) {
				Result late;
				late.api = api;
				late.frames = g_frames.load();
				late.error = "no result within " + std::to_string(timeout) + " s (after " + std::to_string(late.frames) + " frames)";
				print(late);
				std::_Exit(3);
			}
			std::this_thread::sleep_for(std::chrono::milliseconds(100));
		}
	}).detach();

	int code = 0;
	try {
		if (api == "none") {
			run_instance_only(result);
		} else {
			run(result, api == "vulkan", frames);
		}
	} catch (const std::exception &e) {
		result.ok = false;
		result.error = e.what();
		code = 1;
	}
	if (g_instance != XR_NULL_HANDLE) {
		xrDestroyInstance(g_instance);
	}
	done = true;
	print(result);
	return code;
}
