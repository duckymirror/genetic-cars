﻿using System;
using System.Diagnostics;
using System.Reflection;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using FarseerPhysics.Dynamics.Joints;
using FarseerPhysics.Factories;
using log4net;
using Microsoft.Xna.Framework;
using SFML.Graphics;
using SFML.Window;

namespace Genetic_Cars
{
  /// <summary>
  /// Holds the graphics and physics objects for a car.
  /// </summary>
  sealed class Car : IDisposable, IDrawable
  {
    private static readonly ILog Log = LogManager.GetLogger(
      MethodBase.GetCurrentMethod().DeclaringType);

    // collision category for all car components
    public static readonly Category CollisionCategory = Category.Cat2;

    // graphical properties for the car
    private static readonly Color OutlineColor = Color.Black;
    private const float OutlineThickness = -.05f;
    private const float WheelAxisLineThickness = .04f;

    // cars are accelerated over a period of time to try to avoid wheelies
    // delta time for acceleration in seconds
    private const float AccelerationTime = 5;
    // time between each acceleration step
    private const float AccelerationInterval = 0.1f;
    // total number of acceleration steps
    private static readonly int AccelerationSteps =
      (int)Math.Round(AccelerationTime / AccelerationInterval);
    
    /// <summary>
    /// The position where all cars are generated.
    /// </summary>
    public static Vector2f StartPosition { get; set; }

    private bool m_disposed = false;
    private readonly IPhysicsManager m_physicsManager;

    // graphics fields
    private ConvexShape m_bodyShape;
    private CircleShape[] m_wheelShapes;
    private RectangleShape[] m_wheelLines;
    // physics fields
    private Body m_bodyBody;
    private Body[] m_wheelBodies;
    private RevoluteJoint[] m_wheelJoints;
    // acceleration fields
    private float m_accelerationTime;
    private float m_torqueStep;

    /// <summary>
    /// Builds a car.
    /// </summary>
    /// <param name="def">The parameters used to generate the car.</param>
    /// <param name="physics">The program physics system.</param>
    public Car(CarDef def, IPhysicsManager physics)
    {
      if (physics == null)
      {
        throw new ArgumentNullException("physics");
      }
      if (def == null)
      {
        throw new ArgumentNullException("def");
      }
      m_physicsManager = physics;
      
      // will throw on failure
      def.Validate();
      Definition = def;

      CreateBody();
      CreateWheels();

      physics.PostStep += SyncPosition;
    }

    ~Car()
    {
      Dispose(false);
    }

    /// <summary>
    /// The car definition used to build this car.
    /// </summary>
    public CarDef Definition { get; private set; }

    /// <summary>
    /// The geometric center of the car's body.
    /// </summary>
    public Vector2f Center
    {
      get { return m_bodyShape.Position; }
    }

    public void Draw(RenderTarget target)
    {
      for (int i = 0; i < m_wheelShapes.Length; i++)
      {
        target.Draw(m_wheelShapes[i]);
        target.Draw(m_wheelLines[i]);
      }
      target.Draw(m_bodyShape);
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposeManaged)
    {
      if (m_disposed)
      {
        return;
      }

      if (disposeManaged)
      {
        m_bodyShape.Dispose();

        for (int i = 0; i < m_wheelShapes.Length; i++)
        {
          m_wheelShapes[i].Dispose();
          m_wheelLines[i].Dispose();
        }
      }

      m_physicsManager.PreStep -= ApplyAcceleration;
      m_physicsManager.PostStep -= SyncPosition;

      for (int i = 0; i < m_wheelShapes.Length; i++)
      {
        m_physicsManager.World.RemoveJoint(m_wheelJoints[i]);
        m_physicsManager.World.RemoveBody(m_wheelBodies[i]);
      }
      m_physicsManager.World.RemoveBody(m_bodyBody);

      m_disposed = true;
    }

    /// <summary>
    /// Creates the graphics and physics objects for the body of the car.
    /// </summary>
    private void CreateBody()
    {
      var density = Definition.CalcBodyDensity();
      var densityFraction = density / CarDef.MaxBodyDensity;
      // greater density = darker color
      var color = (byte) (255 - (125 * densityFraction));

      m_bodyShape = new ConvexShape((uint)CarDef.NumBodyPoints)
      {
        FillColor = new Color(color, 0, 0),
        OutlineColor = OutlineColor,
        OutlineThickness = OutlineThickness,
        Position = StartPosition.InvertY()
      };

      // build the vertex list for the polygon
      var vertices = new Vertices(CarDef.NumBodyPoints);
      var angleStep = 360f / CarDef.NumBodyPoints;
      var angle = 0f;
      for (int i = 0; i < CarDef.NumBodyPoints; i++)
      {
        // the distance this point is from the center
        var distance = Definition.CalcBodyPoint(i);
        // turn the distance into a point centered around (0,0)
        var point = new Vector2f
        {
          X = distance * (float)Math.Cos(MathExtensions.DegToRad(angle)),
          Y = distance * (float)Math.Sin(MathExtensions.DegToRad(angle))
        };
        m_bodyShape.SetPoint((uint)i, point.InvertY());
        vertices.Add(point.ToVector2());

        angle += angleStep;
      }

      // build the physics shape
      m_bodyBody = BodyFactory.CreatePolygon(
        m_physicsManager.World, vertices, density, StartPosition.ToVector2());
      m_bodyBody.BodyType = BodyType.Dynamic;
      m_bodyBody.CollidesWith = ~CollisionCategory;
      m_bodyBody.CollisionCategories = CollisionCategory;
    }

    /// <summary>
    /// Creates the wheels for the car.  Must be called after CreateBody.
    /// </summary>
    private void CreateWheels()
    {
      Debug.Assert(m_bodyShape != null);
      Debug.Assert(m_bodyBody != null);

      m_wheelShapes = new CircleShape[CarDef.NumWheels];
      m_wheelLines = new RectangleShape[CarDef.NumWheels];
      m_wheelBodies = new Body[CarDef.NumWheels];
      m_wheelJoints = new RevoluteJoint[CarDef.NumWheels];

      for (int i = 0; i < m_wheelShapes.Length; i++)
      {
        // the offset of the attachment point from the center of the main body
        var attachOffset =
          m_bodyShape.GetPoint((uint) Definition.WheelAttachment[i]);
        // the world position of the attachment point
        var attachPos = attachOffset + m_bodyShape.Position;
        var radius = Definition.CalcWheelRadius(i);
        var density = Definition.CalcWheelDensity(i);
        var densityFraction = density / CarDef.MaxWheelDensity;
        // greater density = darker color
        byte color = (byte)(255 - (210 * densityFraction));

        var shape = new CircleShape
        {
          FillColor = new Color(color, color, color),
          OutlineColor = OutlineColor,
          OutlineThickness = OutlineThickness,
          Origin = new Vector2f(radius, radius),
          Position = attachPos,
          Radius = radius
        };
        var line = new RectangleShape
        {
          FillColor = Color.Black,
          Origin = new Vector2f(0, -WheelAxisLineThickness / 2f),
          Size = new Vector2f(WheelAxisLineThickness, radius - WheelAxisLineThickness),
          Position = shape.Position
        };

        var body = BodyFactory.CreateCircle(
          m_physicsManager.World, radius, density, 
          attachPos.ToVector2().InvertY()
          );
        body.BodyType = BodyType.Dynamic;
        body.Friction = 1;
        body.CollidesWith = ~CollisionCategory;
        body.CollisionCategories = CollisionCategory;

        // need to catch the first collision of this body
        body.OnCollision += WheelInitialCollision;

        var joint = new RevoluteJoint(
          m_bodyBody, attachOffset.ToVector2().InvertY(), body,
          new Vector2(0, 0))
        {
          MotorEnabled = false, 
          MaxMotorTorque = m_torqueStep, 
          // speed must be negative for clockwise rotation
          MotorSpeed = -(float)MathExtensions.DegToRad(Definition.CalcWheelSpeed())
        };
        m_physicsManager.World.AddJoint(joint);

        m_wheelShapes[i] = shape;
        m_wheelLines[i] = line;
        m_wheelBodies[i] = body;
        m_wheelJoints[i] = joint;
      }
    }

    /// <summary>
    /// Called on the first collision for either wheel (should always be with 
    /// the track) to enable the motors, and starts the wheel acceleration.
    /// </summary>
    /// <param name="fixtureA"></param>
    /// <param name="fixtureB"></param>
    /// <param name="contact"></param>
    /// <returns></returns>
    private bool WheelInitialCollision(
      Fixture fixtureA, Fixture fixtureB, Contact contact)
    {
      for (int i = 0; i < m_wheelBodies.Length; i++)
      {
        m_wheelJoints[i].MotorEnabled = true;
        m_wheelBodies[i].OnCollision -= WheelInitialCollision;
      }

      m_torqueStep = Definition.CalcWheelTorque() / AccelerationSteps;
      m_accelerationTime = 0;
      m_physicsManager.PreStep += ApplyAcceleration;

      return true;
    }

    /// <summary>
    /// Syncs the position of the car's graphical elements to the physics 
    /// objects.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="deltaTime"></param>
    private void SyncPosition(object sender, float deltaTime)
    {
      var pos = m_bodyBody.Position.ToVector2f().InvertY();
      m_bodyShape.Position = pos;
      m_bodyShape.Rotation = (float)-MathExtensions.RadToDeg(m_bodyBody.Rotation);

      for (int i = 0; i < m_wheelShapes.Length; i++)
      {
        var wheelPos = m_wheelBodies[i].Position.ToVector2f().InvertY();
        var wheelRot = (float)-MathExtensions.RadToDeg(m_wheelBodies[i].Rotation);

        m_wheelShapes[i].Position = wheelPos;
        m_wheelLines[i].Position = wheelPos;
        m_wheelLines[i].Rotation = wheelRot;
      }
    }

    /// <summary>
    /// Increases the torque for each wheel until the defined max torque is 
    /// reached.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="deltaTime"></param>
    private void ApplyAcceleration(object sender, float deltaTime)
    {
      var done = false;
      m_accelerationTime += deltaTime;

      while (m_accelerationTime >= AccelerationInterval)
      {
        m_accelerationTime -= AccelerationInterval;
        foreach (var joint in m_wheelJoints)
        {
          joint.MaxMotorTorque += m_torqueStep;
          if (joint.MaxMotorTorque >= Definition.CalcWheelTorque())
          {
            done = true;
          }
        }

        if (done)
        {
          m_physicsManager.PreStep -= ApplyAcceleration;
          break;
        }
      }
    }
  }
}
