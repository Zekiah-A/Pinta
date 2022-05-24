// 
// SelectTool.cs
//  
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
// 
// Copyright (c) 2010 Jonathan Pobst
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Gdk;
using Gtk;
using Pinta.Core;

namespace Pinta.Tools
{
	public abstract class SelectTool : BaseTool
	{
		private readonly IToolService tools;
		private readonly IWorkspaceService workspace;

		private bool is_drawing = false;
		private PointD shape_origin;
		private PointD reset_origin;
		private PointD shape_end;
		private Gdk.Rectangle last_dirty;
		private SelectionHistoryItem? hist;
		private CombineMode combine_mode;

		private readonly MoveHandle[] handles = new MoveHandle[8];
		private int? active_handle;
		private CursorType? active_cursor;

		public override Gdk.Key ShortcutKey { get { return Gdk.Key.S; } }
		protected override bool ShowAntialiasingButton { get { return false; } }
		public override IEnumerable<MoveHandle> Handles => handles;

		public SelectTool (IServiceManager services) : base (services)
		{
			tools = services.GetService<IToolService> ();
			workspace = services.GetService<IWorkspaceService> ();

			handles[0] = new MoveHandle { Cursor = CursorType.TopLeftCorner };
			handles[1] = new MoveHandle { Cursor = CursorType.BottomLeftCorner };
			handles[2] = new MoveHandle { Cursor = CursorType.TopRightCorner };
			handles[3] = new MoveHandle { Cursor = CursorType.BottomRightCorner };
			handles[4] = new MoveHandle { Cursor = CursorType.LeftSide };
			handles[5] = new MoveHandle { Cursor = CursorType.TopSide };
			handles[6] = new MoveHandle { Cursor = CursorType.RightSide };
			handles[7] = new MoveHandle { Cursor = CursorType.BottomSide };

			workspace.SelectionChanged += AfterSelectionChange;
		}

		protected abstract void DrawShape (Document document, Cairo.Rectangle r, Layer l);

		protected override void OnBuildToolBar (Toolbar tb)
		{
			base.OnBuildToolBar (tb);

			workspace.SelectionHandler.BuildToolbar (tb, Settings);
		}

		protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
		{
			// Ignore extra button clicks while drawing
			if (is_drawing)
				return;

			hist = new SelectionHistoryItem (Icon, Name);
			hist.TakeSnapshot ();

			reset_origin = e.WindowPoint;
			active_handle = FindHandleIndexUnderPoint (e.WindowPoint);

			if (!active_handle.HasValue) {
				combine_mode = PintaCore.Workspace.SelectionHandler.DetermineCombineMode (e);

				var x = Math.Round (Utility.Clamp (e.PointDouble.X, 0, document.ImageSize.Width - 1));
				var y = Math.Round (Utility.Clamp (e.PointDouble.Y, 0, document.ImageSize.Height - 1));
				shape_origin = new PointD (x, y);

				document.PreviousSelection.Dispose ();
				document.PreviousSelection = document.Selection.Clone ();
				document.Selection.SelectionPolygons.Clear ();

				// The bottom right corner should be selected.
				active_handle = 3;
			}

			// Do a full redraw for modes that can wipe existing selections outside the rectangle being drawn.
			if (combine_mode == CombineMode.Replace || combine_mode == CombineMode.Intersect) {
				var size = document.ImageSize;
				last_dirty = new Gdk.Rectangle (0, 0, size.Width, size.Height);
			}

			is_drawing = true;
		}

		protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
		{
			if (!is_drawing) {
				UpdateCursor (document, e.WindowPoint);
				return;
			}

			var x = Math.Round (Utility.Clamp (e.PointDouble.X, 0, document.ImageSize.Width));
			var y = Math.Round (Utility.Clamp (e.PointDouble.Y, 0, document.ImageSize.Height));

			// Should always be true, set in OnMouseDown
			if (active_handle.HasValue)
				OnHandleMoved (active_handle.Value, x, y, e.IsShiftPressed);

			var dirty = ReDraw (document);

			UpdateHandlePositions ();

			if (document.Selection != null) {
				SelectionModeHandler.PerformSelectionMode (combine_mode, document.Selection.SelectionPolygons);
				document.Workspace.Invalidate (dirty.Union (last_dirty));
			}

			last_dirty = dirty;
		}

		protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
		{
			// If the user didn't move the mouse, they want to deselect
			var tolerance = 0;

			if (Math.Abs (reset_origin.X - e.WindowPoint.X) <= tolerance && Math.Abs (reset_origin.Y - e.WindowPoint.Y) <= tolerance) {
				// Mark as being done interactive drawing before invoking the deselect action.
				// This will allow AfterSelectionChanged() to clear the selection.
				is_drawing = false;

				if (hist != null) {
					// Roll back any changes made to the selection, e.g. in OnMouseDown().
					hist.Undo ();

					hist.Dispose ();
					hist = null;
				}

				PintaCore.Actions.Edit.Deselect.Activate ();

			} else {
				var dirty = ReDraw (document);

				if (document.Selection != null) {
					SelectionModeHandler.PerformSelectionMode (combine_mode, document.Selection.SelectionPolygons);

					document.Selection.Origin = shape_origin;
					document.Selection.End = shape_end;
					document.Workspace.Invalidate (last_dirty.Union (dirty));
					last_dirty = dirty;
				}
				if (hist != null) {
					document.History.PushNewItem (hist);
					hist = null;
				}
			}

			is_drawing = false;
			active_handle = null;

			// Update the mouse cursor.
			UpdateCursor (document, e.WindowPoint);
		}

		protected override void OnActivated (Document? document)
		{
			base.OnActivated (document);

			// When entering the tool, update the selection handles from the
			// document's current selection.
			if (document is not null) {
				LoadFromDocument (document);
			}
		}

		protected override void OnSaveSettings (ISettingsService settings)
		{
			base.OnSaveSettings (settings);

			workspace.SelectionHandler.OnSaveSettings (settings);
		}

		private void OnHandleMoved (int handle, double x, double y, bool shift_pressed)
		{
			switch (handle) {
				case 0:
					shape_origin.X = x;
					shape_origin.Y = y;
					if (shift_pressed) {
						if (shape_end.X - shape_origin.X <= shape_end.Y - shape_origin.Y)
							shape_origin.X = shape_end.X - shape_end.Y + shape_origin.Y;
						else
							shape_origin.Y = shape_end.Y - shape_end.X + shape_origin.X;
					}
					break;
				case 1:
					shape_origin.X = x;
					shape_end.Y = y;
					if (shift_pressed) {
						if (shape_end.X - shape_origin.X <= shape_end.Y - shape_origin.Y)
							shape_origin.X = shape_end.X - shape_end.Y + shape_origin.Y;
						else
							shape_end.Y = shape_origin.Y + shape_end.X - shape_origin.X;
					}
					break;
				case 2:
					shape_end.X = x;
					shape_origin.Y = y;
					if (shift_pressed) {
						if (shape_end.X - shape_origin.X <= shape_end.Y - shape_origin.Y)
							shape_end.X = shape_origin.X + shape_end.Y - shape_origin.Y;
						else
							shape_origin.Y = shape_end.Y - shape_end.X + shape_origin.X;
					}
					break;
				case 3:
					shape_end.X = x;
					shape_end.Y = y;
					if (shift_pressed) {
						if (shape_end.X - shape_origin.X <= shape_end.Y - shape_origin.Y)
							shape_end.X = shape_origin.X + shape_end.Y - shape_origin.Y;
						else
							shape_end.Y = shape_origin.Y + shape_end.X - shape_origin.X;
					}
					break;
				case 4:
					shape_origin.X = x;
					if (shift_pressed) {
						var d = shape_end.X - shape_origin.X;
						shape_origin.Y = (shape_origin.Y + shape_end.Y - d) / 2;
						shape_end.Y = (shape_origin.Y + shape_end.Y + d) / 2;
					}
					break;
				case 5:
					shape_origin.Y = y;
					if (shift_pressed) {
						var d = shape_end.Y - shape_origin.Y;
						shape_origin.X = (shape_origin.X + shape_end.X - d) / 2;
						shape_end.X = (shape_origin.X + shape_end.X + d) / 2;
					}
					break;
				case 6:
					shape_end.X = x;
					if (shift_pressed) {
						var d = shape_end.X - shape_origin.X;
						shape_origin.Y = (shape_origin.Y + shape_end.Y - d) / 2;
						shape_end.Y = (shape_origin.Y + shape_end.Y + d) / 2;
					}
					break;
				case 7:
					shape_end.Y = y;
					if (shift_pressed) {
						var d = shape_end.Y - shape_origin.Y;
						shape_origin.X = (shape_origin.X + shape_end.X - d) / 2;
						shape_end.X = (shape_origin.X + shape_end.X + d) / 2;
					}
					break;
				default:
					throw new ArgumentOutOfRangeException ("handle");
			}
		}

		private void UpdateHandlePositions ()
		{
			Gdk.Rectangle ComputeHandleBounds ()
			{
				// When loading a new document, we might get a selection change event
				// before there is a canvas size / scale.
				if (PintaCore.Workspace.CanvasSize.IsEmpty)
					return Gdk.Rectangle.Zero;

				return handles.Select (c => c.InvalidateRect).Aggregate ((accum, r) => accum.Union (r));
			}

			var dirty = ComputeHandleBounds ();
			handles[0].CanvasPosition = new PointD (shape_origin.X, shape_origin.Y);
			handles[1].CanvasPosition = new PointD (shape_origin.X, shape_end.Y);
			handles[2].CanvasPosition = new PointD (shape_end.X, shape_origin.Y);
			handles[3].CanvasPosition = new PointD (shape_end.X, shape_end.Y);
			handles[4].CanvasPosition = new PointD (shape_origin.X, (shape_origin.Y + shape_end.Y) / 2);
			handles[5].CanvasPosition = new PointD ((shape_origin.X + shape_end.X) / 2, shape_origin.Y);
			handles[6].CanvasPosition = new PointD (shape_end.X, (shape_origin.Y + shape_end.Y) / 2);
			handles[7].CanvasPosition = new PointD ((shape_origin.X + shape_end.X) / 2, shape_end.Y);
			dirty = dirty.Union (ComputeHandleBounds ());

			// Repaint at the old and new handle positions.
			PintaCore.Workspace.InvalidateWindowRect (dirty);
		}

		private Gdk.Rectangle ReDraw (Document document)
		{
			document.Selection.Visible = true;
			ShowHandles (true);

			var rect = CairoExtensions.PointsToRectangle (shape_origin, shape_end);

			DrawShape (document, rect, document.Layers.SelectionLayer);

			// Figure out a bounding box for everything that was drawn, and add a bit of padding.
			var dirty = rect.ToGdkRectangle ();
			dirty.Inflate (2, 2);
			return dirty;
		}

		private void ShowHandles (bool visible)
		{
			foreach (var handle in handles)
				handle.Active = visible;
		}

		private MoveHandle? FindHandleUnderPoint (PointD window_point)
		{
			return handles.FirstOrDefault (c => c.Active && c.ContainsPoint (window_point));
		}

		private int? FindHandleIndexUnderPoint (PointD window_point)
		{
			var handle = FindHandleUnderPoint (window_point);
			if (handle is not null) {
				return Array.IndexOf (handles, handle);
			} else {
				return null;
			}
		}

		private void UpdateCursor (Document document, PointD window_point)
		{
			var active_handle = FindHandleUnderPoint (window_point);
			if (active_handle is not null) {
				SetCursor (new Cursor (active_handle.Cursor));
				active_cursor = active_handle.Cursor;
				return;
			}

			if (active_cursor.HasValue) {
				SetCursor (DefaultCursor);
				active_cursor = null;
			}
		}

		protected override void OnAfterUndo (Document document)
		{
			base.OnAfterUndo (document);
			LoadFromDocument (document);
		}

		protected override void OnAfterRedo (Document document)
		{
			base.OnAfterRedo (document);
			LoadFromDocument (document);
		}

		private void AfterSelectionChange (object? sender, EventArgs event_args)
		{
			if (is_drawing || !workspace.HasOpenDocuments)
				return;

			// TODO: Try to remove this ActiveDocument call
			LoadFromDocument (workspace.ActiveDocument);
		}

		/// <summary>
		/// Initialize from the document's selection.
		/// </summary>
		private void LoadFromDocument (Document document)
		{
			var selection = document.Selection;
			shape_origin = selection.Origin;
			shape_end = selection.End;
			ShowHandles (document.Selection.Visible);

			if (tools.CurrentTool == this) {
				UpdateHandlePositions ();
				document.Workspace.Invalidate ();
			}
		}
	}
}
