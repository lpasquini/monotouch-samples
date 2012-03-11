using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.ES20;
using MonoTouch.GLKit;
using MonoTouch.OpenGLES;
using MonoTouch.Foundation;
using MonoTouch.CoreFoundation;
using MonoTouch.CoreMedia;
using MonoTouch.UIKit;
using MonoTouch.AVFoundation;
using MonoTouch.CoreVideo;
using System.IO;
using System.Runtime.InteropServices;

namespace GLCameraRipple
{
	public class RippleViewController : GLKViewController {
		DataOutputDelegate dataOutputDelegate;
		EAGLContext context;
		Size size;
		int meshFactor;
		NSString sessionPreset;
		CVOpenGLESTextureCache videoTextureCache;
		AVCaptureSession session;
		GLKView glkView;
		
		//
		// OpenGL components
		//
		const int UNIFORM_Y = 0;
		const int UNIFORM_UV = 1;
		const int ATTRIB_VERTEX = 0;
    	const int ATTRIB_TEXCOORD = 1;

		int [] uniforms = new int [2];
		int program;
		
		UILabel label, label2;
		
		public override void ViewDidLoad ()
		{
			base.ViewDidLoad ();

			context = new EAGLContext (EAGLRenderingAPI.OpenGLES2);
			glkView = (GLKView) View;
			label = new UILabel (new RectangleF (0, 0, 320, 30)){
				BackgroundColor = UIColor.White,
				TextColor = UIColor.Black
			};
			label2 = new UILabel (new RectangleF (0, 40, 320, 30)){
				BackgroundColor = UIColor.White,
				TextColor = UIColor.Black
			};
			glkView.AddSubview (label);
			glkView.AddSubview (label2);
			           
			glkView.Context = context;
			glkView.DrawInRect += Draw;
			
			PreferredFramesPerSecond = 10;
			size = UIScreen.MainScreen.Bounds.Size.ToSize ();
			View.ContentScaleFactor = UIScreen.MainScreen.Scale;
			
			if (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad){
				meshFactor = 8;
				sessionPreset = AVCaptureSession.PresetiFrame1280x720;
			} else {
				meshFactor = 4;
				sessionPreset = AVCaptureSession.Preset640x480;
			}
			SetupGL ();
			SetupAVCapture ();
		}
		
		void Draw (object sender, GLKViewDrawEventArgs args)
		{
			Console.WriteLine ("GL Draw");
			GL.Clear ((int)All.ColorBufferBit);
			if (ripple != null){
				short s = 0;
				GL.DrawElements (All.TriangleStrip, ripple.IndexCount, All.UnsignedShort, IntPtr.Zero);
			}
		}

		public override void Update ()
		{
			Console.WriteLine ("GL UPdate");
			if (ripple != null){
				ripple.RunSimulation ();
				unsafe {GL.BufferData (All.ArrayBuffer, (IntPtr) ripple.VertexSize, (IntPtr)ripple.TexCoords, All.DynamicDraw);}
			}
		}
		
		public override void ViewDidUnload ()
		{
			base.ViewDidUnload ();
			TeardownAVCapture ();
			TeardownGL ();
			if (EAGLContext.CurrentContext == context)
				EAGLContext.SetCurrentContext (null);
		}
		
		public override bool ShouldAutorotateToInterfaceOrientation (UIInterfaceOrientation toInterfaceOrientation)
		{
			// Camera is fixed, only allow portrait
			return (toInterfaceOrientation == UIInterfaceOrientation.Portrait);
		}
		
		void SetupGL ()
		{
			EAGLContext.SetCurrentContext (context);
			if (LoadShaders ()){
				GL.UseProgram (program);
				GL.Uniform1 (uniforms [UNIFORM_Y], 0);
				GL.Uniform1 (uniforms [UNIFORM_UV], 1);
			}
		}
		
		void SetupAVCapture ()
		{
			if ((videoTextureCache = CVOpenGLESTextureCache.FromEAGLContext (context)) == null){
				Console.WriteLine ("Could not create the CoreVideo TextureCache");
				return;
			}
			session = new AVCaptureSession ();
			session.BeginConfiguration ();
			
			// Preset size
			session.SessionPreset = sessionPreset;
			
			// Input device
			var videoDevice = AVCaptureDevice.DefaultDeviceWithMediaType (AVMediaType.Video);
			if (videoDevice == null){
				Console.WriteLine ("No video device");
				return;
			}
			NSError err;
			var input = new AVCaptureDeviceInput (videoDevice, out err);
			if (err != null){
				Console.WriteLine ("Error creating video capture device");
				return;
			}
			session.AddInput (input);
			
			// Create the output device
			var dataOutput = new AVCaptureVideoDataOutput () {
				AlwaysDiscardsLateVideoFrames = true,
				
				// YUV 420
				VideoSettings = new AVVideoSettings (CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange)
			};
			dataOutputDelegate = new DataOutputDelegate (this);

			// 
			// This dispatches the video frames into the main thread, because the OpenGL
			// code is accessing the data synchronously.
			//
			dataOutput.SetSampleBufferDelegateAndQueue (dataOutputDelegate, DispatchQueue.MainQueue);
			session.AddOutput (dataOutput);
			session.CommitConfiguration ();
			session.StartRunning ();
		}
		
		void TeardownAVCapture ()
		{
		}
		
		void TeardownGL ()
		{
		}
		
		bool LoadShaders ()
		{
			int vertShader, fragShader;
			
			program = GL.CreateProgram ();
			if (CompileShader (out vertShader, All.VertexShader, "Shaders/Shader.vsh")){
				if (CompileShader (out fragShader, All.FragmentShader, "Shaders/Shader.fsh")){
					// Attach shaders
					GL.AttachShader (program, vertShader);
					GL.AttachShader (program, fragShader);
					
					// Bind attribtue locations
					GL.BindAttribLocation (program, ATTRIB_VERTEX, "position");
					GL.BindAttribLocation (program, ATTRIB_TEXCOORD, "texCoord");

					if (LinkProgram (program)){
						// Get uniform locations
						uniforms [UNIFORM_Y] = GL.GetUniformLocation (program, "SamplerY");
						uniforms [UNIFORM_UV] = GL.GetUniformLocation (program, "SampleUV");
						
						// Delete these ones, we do not need them anymore
						GL.DeleteShader (vertShader);
						GL.DeleteShader (fragShader);
						return true;
					} else {
						Console.WriteLine ("Failed to link the shader programs");
						GL.DeleteProgram (program);
						program = 0;
					}
				} else
					Console.WriteLine ("Failed to compile fragment shader");
				GL.DeleteShader (vertShader);
			} else 
				Console.WriteLine ("Failed to compile vertex shader");
			GL.DeleteProgram (program);
			return false;
		}
		
		bool CompileShader (out int shader, All type, string path)
		{
			string shaderProgram = File.ReadAllText (path);
			int len = shaderProgram.Length, status = 0;
			shader = GL.CreateShader (type);
			
			GL.ShaderSource (shader, 1, new string [] { shaderProgram }, ref len);
			GL.CompileShader (shader);
			GL.GetShader (shader, All.CompileStatus, ref status);
			if (status == 0){
				GL.DeleteShader (shader);
				return false;
			}
			return true;
		}
		
		bool LinkProgram (int program)
		{
			GL.LinkProgram (program);
			int status = 0;
			int len = 0;
			GL.GetProgram (program, All.LinkStatus, ref status);
			if (status == 0){
				GL.GetProgram (program, All.InfoLogLength, ref len);
				var sb = new System.Text.StringBuilder (len);
				GL.GetProgramInfoLog (program, len, ref len, sb);
				Console.WriteLine ("Link error: {0}", sb);
			}
			return status != 0;
		}
			
		int indexVbo, positionVbo, texcoordVbo;
		
		unsafe void SetupBuffers ()
		{
			GL.GenBuffers (1, ref indexVbo);
			GL.BindBuffer (All.ElementArrayBuffer, indexVbo);
			GL.BufferData (All.ElementArrayBuffer, (IntPtr) ripple.IndexSize, (IntPtr) ripple.Indices, All.StaticDraw);
			
			GL.GenBuffers (1, ref positionVbo);
			GL.BindBuffer (All.ArrayBuffer, positionVbo);
			GL.BufferData (All.ArrayBuffer, (IntPtr) ripple.VertexSize, (IntPtr) ripple.Vertices, All.StaticDraw);
			
			GL.EnableVertexAttribArray (ATTRIB_VERTEX);
			
			GL.VertexAttribPointer (ATTRIB_VERTEX, 2, All.Float, false, 2*sizeof(float), IntPtr.Zero);
			GL.GenBuffers (1, ref texcoordVbo);
			GL.BindBuffer (All.ArrayBuffer, texcoordVbo);
			GL.BufferData (All.ArrayBuffer, (IntPtr) ripple.VertexSize, (IntPtr) ripple.TexCoords, All.DynamicDraw);
			
			GL.EnableVertexAttribArray (ATTRIB_TEXCOORD);
			GL.VertexAttribPointer (ATTRIB_TEXCOORD, 2, All.Float, false, 2*sizeof (float), IntPtr.Zero);
		}
			  
		RippleModel ripple;
		
		void SetupRipple (int width, int height)
		{			
			ripple = new RippleModel (size, meshFactor, 5, new Size (width, height));
			SetupBuffers ();
		}
		
		class DataOutputDelegate : AVCaptureVideoDataOutputSampleBufferDelegate {
			CVOpenGLESTexture lumaTexture, chromaTexture;
			RippleViewController container;
			int textureWidth, textureHeight;
			
			public DataOutputDelegate (RippleViewController container)
			{
				this.container = container;
			}
			
			void CleanupTextures ()
			{
				if (lumaTexture != null)
					lumaTexture.Dispose ();
				if (chromaTexture != null)
					chromaTexture.Dispose ();
				container.videoTextureCache.Flush (CVOptionFlags.None);
			}
		
			public override void DidOutputSampleBuffer (AVCaptureOutput captureOutput, MonoTouch.CoreMedia.CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
			{
				try {
					using (var pixelBuffer = sampleBuffer.GetImageBuffer () as CVPixelBuffer){	
						int width = pixelBuffer.Width;
						int height = pixelBuffer.Height;
					
						if (container.ripple == null || width != textureWidth || height != textureHeight){
							textureWidth = width;
							textureHeight = height;
							container.SetupRipple (textureWidth, textureHeight);
						}
						CleanupTextures ();
						
						// Y-plane
						GL.ActiveTexture (All.Texture0);
						All re = (All) 0x1903; // GL_RED_EXT
						CVReturn status;
						lumaTexture = container.videoTextureCache.TextureFromImage (pixelBuffer, true, re, textureWidth, textureHeight, re, DataType.UnsignedByte, 0, out status);
						
						if (lumaTexture == null){
							Console.WriteLine ("Error creating luma texture: {0}", status);
							return;
						}
						GL.BindTexture ((All)lumaTexture.Target, lumaTexture.Name);
						GL.TexParameter (All.Texture2D, All.TextureWrapS, (int) All.ClampToEdge);
						GL.TexParameter (All.Texture2D, All.TextureWrapT, (int) All.ClampToEdge);
						
						// UV Plane
						GL.ActiveTexture (All.Texture1);
						re = (All) 0x8227; // GL_RG_EXT
						chromaTexture = container.videoTextureCache.TextureFromImage (pixelBuffer, true, re, textureWidth/2, textureHeight/2, re, DataType.UnsignedByte, 1, out status);
						if (chromaTexture == null){
							Console.WriteLine ("Error creating chroma texture: {0}", status);
							return;
						}
						GL.BindTexture ((All) chromaTexture.Target, chromaTexture.Name);
						GL.TexParameter (All.Texture2D, All.TextureWrapS, (int)All.ClampToEdge);
						GL.TexParameter (All.Texture2D, All.TextureWrapT, (int) All.ClampToEdge);
					}
				} finally {
					sampleBuffer.Dispose ();
				}
			}
		}
	}
}