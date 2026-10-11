using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Dropshelf.Animation;

/// <summary>
/// Procedural teardrop: a profile curve revolved around the Y axis. The tip points up (+Y), the round belly is at the
/// bottom, the model spans roughly y = -1..1. Profile (theta 0 = tip, pi = bottom): r = K sin(theta) sin(theta/2)^P,
/// y = cos(theta). Normals come from the analytic profile derivative, so shading is smooth with no faceting.
/// </summary>
internal static class TeardropMesh
{
    private const double K = 0.95;
    private const double P = 1.15;

    /// <summary>Radius and height of the profile at <paramref name="theta"/> (0 = tip, pi = bottom).</summary>
    public static (double R, double Y) Profile(double theta)
    {
        double r = K * Math.Sin(theta) * Math.Pow(Math.Sin(theta / 2), P);
        return (r, Math.Cos(theta));
    }

    /// <summary>Profile radius at height <paramref name="y"/> (-1..1).</summary>
    public static double RadiusAtHeight(double y) => Profile(Math.Acos(Math.Clamp(y, -1, 1))).R;

    public static MeshGeometry3D Create(int slices = 32, int stacks = 24)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection((slices + 1) * (stacks + 1));
        var normals = new Vector3DCollection((slices + 1) * (stacks + 1));
        var texture = new PointCollection((slices + 1) * (stacks + 1));

        for (int j = 0; j <= stacks; j++)
        {
            double theta = Math.PI * j / stacks;
            var (r, y) = Profile(theta);

            // Outward normal in the (r, y) plane: (-dy/dtheta, dr/dtheta), normalised.
            double s = Math.Sin(theta / 2);
            double dr = K * (Math.Cos(theta) * Math.Pow(s, P) + Math.Sin(theta) * P * Math.Pow(s, P - 1) * Math.Cos(theta / 2) / 2);
            double dy = -Math.Sin(theta);
            double nr = -dy;
            double ny = dr;
            double len = Math.Sqrt(nr * nr + ny * ny);
            if (len < 1e-6 || theta < 1e-6)
            {
                nr = 0;
                ny = 1; // the tip
            }
            else
            {
                nr /= len;
                ny /= len;
            }

            if (j == stacks)
            {
                nr = 0;
                ny = -1; // the bottom pole
            }

            for (int i = 0; i <= slices; i++)
            {
                double phi = 2 * Math.PI * i / slices; // phi = 0 faces the camera (+Z)
                double sinPhi = Math.Sin(phi);
                double cosPhi = Math.Cos(phi);
                positions.Add(new Point3D(r * sinPhi, y, r * cosPhi));
                normals.Add(new Vector3D(nr * sinPhi, ny, nr * cosPhi));
                texture.Add(new Point((double)i / slices, (double)j / stacks));
            }
        }

        var indices = new Int32Collection(slices * stacks * 6);
        int row = slices + 1;
        for (int j = 0; j < stacks; j++)
        {
            for (int i = 0; i < slices; i++)
            {
                int a = j * row + i;
                int b = a + 1;
                int c = a + row;
                int d = c + 1;

                // Counter-clockwise seen from outside.
                indices.Add(a);
                indices.Add(c);
                indices.Add(b);
                indices.Add(b);
                indices.Add(c);
                indices.Add(d);
            }
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TextureCoordinates = texture;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }
}
